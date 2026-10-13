using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Ai;

/// <summary>
/// The shared medical library (PLAN.md 1.3 and PLAN_ADDENDUM.md 7.3). Everything that can enumerate or mutate the
/// installation's library — and the list that exposes the real folder path — belongs to the owner only. A clinic
/// role keeps exactly one door: opening the full file a cited answer points at, through the index row's id.
/// </summary>
[ApiController]
[Route("api/library")]
public class LibraryController : ControllerBase
{
    /// <summary>PLAN_ADDENDUM.md 7.8: per-type size caps — PDFs and JSONL may be large, plain text never needs to be.</summary>
    private const long MaxPdfBytes = 50 * 1024 * 1024;
    private const long MaxTextBytes = 5 * 1024 * 1024;

    private readonly MedicalLibraryService _library;
    private readonly ClinicDbContext _db;

    public LibraryController(MedicalLibraryService library, ClinicDbContext db)
    {
        _library = library;
        _db = db;
    }

    [HttpGet]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> List()
    {
        var indexed = await _db.LibraryDocuments.AsNoTracking()
            .OrderBy(d => d.Title)
            .Select(d => new { d.Id, d.FileName, d.Title, d.FileSize, d.ChunkCount, d.IndexedAtUtc })
            .ToListAsync();

        var indexedNames = indexed.Select(d => d.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = Directory.EnumerateFiles(_library.LibraryPath, "*", SearchOption.AllDirectories)
            .Where(DocumentTextExtractor.IsSupported)
            .Select(f => Path.GetRelativePath(_library.LibraryPath, f))
            .Where(n => !indexedNames.Contains(n))
            .ToList();

        return Ok(new { folder = _library.LibraryPath, documents = indexed, notIndexed = pending, status = _library.GetStatus() });
    }

    [HttpPost("reindex")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Reindex()
    {
        if (!_library.StartReindex())
            return Conflict(new { message = "الفهرسة قيد التنفيذ بالفعل" });

        _db.LogAction("Reindexed", "Library");
        await _db.SaveChangesAsync();
        return Accepted(new { message = "بدأت الفهرسة" });
    }

    [HttpDelete]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Delete([FromQuery] string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "يرجى تحديد الملف" });
        if (!await _library.DeleteAsync(name))
            return Conflict(new { message = "لا يمكن الحذف أثناء الفهرسة" });

        _db.LogAction("Deleted", "Library", name);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("upload")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [RequestSizeLimit(MaxPdfBytes)]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { message = "يرجى اختيار ملف" });
        if (!DocumentTextExtractor.IsSupported(file.FileName ?? ""))
            return BadRequest(new { message = "الملفات المدعومة: PDF و TXT و MD و JSONL" });

        var ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
        var limit = ext == ".pdf" ? MaxPdfBytes : MaxTextBytes;
        if (file.Length > limit)
            return BadRequest(new { message = $"حجم الملف يتجاوز الحد المسموح لهذا النوع ({limit / 1024 / 1024} ميجابايت)" });

        // PLAN_ADDENDUM.md 7.8: the content is validated, not the extension — a .pdf that is not a PDF must be
        // refused before it reaches the library.
        await using (var check = file.OpenReadStream())
        {
            var reason = await ContentSignature.ValidateAsync(ext, check);
            if (reason is not null) return BadRequest(new { message = reason });
        }

        // PLAN_ADDENDUM.md 7.8: the file is stored under a random name, never the caller's — neither "..\..\evil"
        // nor any directory can leave the library folder. The extension is kept so the indexer still knows the type.
        var storedName = $"{Guid.NewGuid():N}{ext}";
        var target = Path.GetFullPath(Path.Combine(_library.LibraryPath, storedName));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_library.LibraryPath)) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "اسم ملف غير صالح" });
        if (System.IO.File.Exists(target))
            return Conflict(new { message = "يوجد ملف بنفس الاسم" });

        await using (var stream = System.IO.File.Create(target))
            await file.CopyToAsync(stream);

        _db.LogAction("Uploaded", "Library", storedName);
        await _db.SaveChangesAsync();
        _library.StartReindex();
        return Ok(new { fileName = storedName });
    }

    /// <summary>
    /// PLAN.md 1.3: the full cited file, for the doctor reading an answer. The file is resolved through the index
    /// row — never through a path the caller supplies — and the response is the file's own bytes only: the
    /// server's folder stays out of it. PLAN.md 1.4 gates it on the assistant permission (default: doctor and
    /// admin), so an account customised out of it loses this door too.
    /// </summary>
    [HttpGet("documents/{id:int}")]
    [Authorize(Policy = Permissions.Assistant)]
    public async Task<IActionResult> OpenDocument(int id)
    {
        var doc = await _db.LibraryDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
        if (doc == null) return NotFound(new { message = "المرجع غير موجود" });

        var relative = doc.FileName.Replace('\\', '/');
        if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return NotFound();
        var path = Path.GetFullPath(Path.Combine(_library.LibraryPath, relative));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_library.LibraryPath)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return NotFound();
        if (!System.IO.File.Exists(path)) return NotFound(new { message = "الملف غير موجود على الخادم" });

        return File(await System.IO.File.ReadAllBytesAsync(path), ContentTypeFor(doc.FileName));
    }

    private static string ContentTypeFor(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".md" => "text/markdown",
            ".jsonl" => "application/json",
            _ => "text/plain"
        };
    }
}
