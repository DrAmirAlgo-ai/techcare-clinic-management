using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Audit;

/// <summary>
/// PLAN_ADDENDUM.md 7.4: the clinic's read-only event trail, for the clinic's admin (their clinic only). The
/// owner's equivalent — every clinic on the installation — lives in <c>OwnerController</c>. Both answer who did
/// what on which entity, when and from which IP; neither can write, and no row ever holds medical content.
/// </summary>
[ApiController]
[Route("api/audit-logs")]
[Authorize(Policy = Permissions.Admin)]
public class AuditController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly TenantContext _tenant;

    public AuditController(ClinicDbContext db, TenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int? userId,
        [FromQuery] int take = 200)
    {
        // AuditLog carries no global query filter, so the clinic scope is applied here, by hand: an admin may
        // read exactly their own clinic's rows and nothing else.
        if (_tenant.ClinicId is not int clinicId)
            return NotFound(new { message = "غير موجود" });

        var query = _db.AuditLogs.AsNoTracking().Where(a => a.ClinicId == clinicId);
        if (fromUtc is DateTime from) query = query.Where(a => a.AtUtc >= from);
        if (toUtc is DateTime to) query = query.Where(a => a.AtUtc <= to);
        if (userId is int uid) query = query.Where(a => a.UserId == uid);

        var items = await query
            .OrderByDescending(a => a.AtUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(a => new { a.Id, a.AtUtc, a.UserId, a.UserName, a.Action, a.Entity, a.EntityId, a.IpAddress })
            .ToListAsync();
        return Ok(new { items });
    }

    /// <summary>
    /// PLAN_ADDENDUM.md 9.2: the AI interaction log — who asked about whom, the sources used and how long it
    /// took. It is a separate store from the event trail above (which never carries medical content), but it is
    /// read through the same admin door, scoped to this clinic the same way.
    /// </summary>
    [HttpGet("ai")]
    public async Task<IActionResult> AiInteractions(
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int? userId,
        [FromQuery] int take = 200)
    {
        if (_tenant.ClinicId is not int clinicId)
            return NotFound(new { message = "غير موجود" });

        var query = _db.AiInteractions.AsNoTracking().Where(a => a.ClinicId == clinicId);
        if (fromUtc is DateTime from) query = query.Where(a => a.AtUtc >= from);
        if (toUtc is DateTime to) query = query.Where(a => a.AtUtc <= to);
        if (userId is int uid) query = query.Where(a => a.UserId == uid);

        var items = await query
            .OrderByDescending(a => a.AtUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(a => new
            {
                a.Id, a.AtUtc, a.UserId, a.PatientId, a.Kind, a.Sources, a.DurationMs, a.QuestionText, a.AnswerText,
                UserName = _db.Users.Where(u => u.Id == a.UserId).Select(u => (string?)u.FullName).FirstOrDefault()
            })
            .ToListAsync();
        return Ok(new { items });
    }
}