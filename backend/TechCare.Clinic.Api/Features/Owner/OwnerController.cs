using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Ai;
using TechCare.Clinic.Api.Infrastructure;
using UglyToad.PdfPig;

namespace TechCare.Clinic.Api.Features.Owner;

/// <summary>
/// The installation owner's panel (PLAN.md 1.1). This is the only place in the API that is allowed to look across
/// clinics, and it is reachable with the SuperAdmin role only — a token that carries no clinic at all, so every
/// clinic-scoped endpoint and query filter stays closed to it.
[ApiController]
[Route("api/owner")]
[Authorize(Roles = Roles.SuperAdmin)]
public class OwnerController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly AiSettingsService _settings;
    private readonly IOptions<AiOptions> _aiDefaults;
    private readonly IOptions<SubscriptionOptions> _subscriptions;
    private readonly IPasswordHasher<User> _hasher;

    public OwnerController(ClinicDbContext db, AiSettingsService settings, IOptions<AiOptions> aiDefaults,
        IOptions<SubscriptionOptions> subscriptions, IPasswordHasher<User> hasher)
    {
        _db = db;
        _settings = settings;
        _aiDefaults = aiDefaults;
        _subscriptions = subscriptions;
        _hasher = hasher;
    }

    /// <summary>
    /// Every clinic on the installation, with the numbers an owner wants at a glance. With
    /// <c>expiringDays=N</c> the list keeps only the clinics whose subscription ends within the next N days
    /// (PLAN.md 10.5), and <c>monthRevenue</c> totals this month's recorded payments across every clinic.
    /// </summary>
    [HttpGet("clinics")]
    public async Task<IActionResult> Clinics([FromQuery] int? expiringDays)
    {
        var clinics = await _db.Clinics.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        if (expiringDays is int days && days > 0)
            clinics = clinics
                .Where(c => SubscriptionInfo.EndsAtUtc(c) is DateTime end && end <= DateTime.UtcNow.AddDays(days))
                .ToList();

        // SQLite cannot aggregate a decimal column (the same reason the per-clinic revenue is summed in memory),
        // so the month's total loads the amounts and adds them up here.
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthAmounts = await _db.SubscriptionPayments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.PaidAtUtc >= monthStart && p.PaidAtUtc < monthStart.AddMonths(1))
            .Select(p => p.Amount)
            .ToListAsync();

        return Ok(new
        {
            items = await SummaryAsync(clinics),
            monthRevenue = monthAmounts.Sum()
        });
    }

    /// <summary>One clinic: its settings plus the accounts that can sign in to it.</summary>
    [HttpGet("clinics/{id:int}")]
    public async Task<IActionResult> ClinicById(int id)
    {
        var clinic = await _db.Clinics.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (clinic == null) return NotFound();

        // Logically deleted accounts (PLAN.md 10.6) stay out of the staff list, same as the clinic's own view.
        var staff = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.ClinicId == id && !u.IsDeleted)
            .OrderBy(u => u.Role).ThenBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName, u.Username, u.Role, u.IsActive, u.CreatedAtUtc })
            .ToListAsync();

        var summary = await SummaryAsync(new List<Domain.Clinic> { clinic });

        // PLAN.md 10.4: the clinic's recorded payments, newest first — the owner's renewal paper trail.
        var payments = await _db.SubscriptionPayments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.ClinicId == id)
            .OrderByDescending(p => p.PaidAtUtc)
            .Take(20)
            .Select(p => new { p.Id, p.Amount, p.PaidAtUtc, p.Note, p.CreatedAtUtc })
            .ToListAsync();

        return Ok(new
        {
            clinic = new
            {
                clinic.Id, clinic.Name, clinic.Slug, clinic.DoctorName, clinic.Specialty, clinic.About,
                clinic.Phone, clinic.Address, clinic.ConsultationFee, clinic.FollowUpFee,
                clinic.AvgMinutesPerPatient, clinic.TimeZoneId, clinic.OnlineBookingEnabled, clinic.CreatedAtUtc,
                // PLAN.md 10.2: the subscription the owner tunes from this screen.
                clinic.Plan, clinic.SubscriptionStatus, clinic.TrialEndsAtUtc, clinic.SubscriptionEndsAtUtc,
                clinic.AiEnabled, clinic.TrialDays
            },
            numbers = summary[0],
            staff,
            payments
        });
    }

    /// <summary>
    /// PLAN.md 10.1 — registers a new clinic from the owner panel: the clinic row with the installation's default
    /// schedule and fees, plus the admin account that must change its temporary password at the first login
    /// (PLAN_ADDENDUM.md 7.5). A slug another clinic already uses answers 409; the unique index on
    /// <see cref="Domain.Clinic.Slug"/> stays the last line of defence against a lost race.
    /// </summary>
    [HttpPost("clinics")]
    public async Task<IActionResult> CreateClinic([FromBody] NewClinicRequest request)
    {
        var name = request.Name.Trim();
        var doctorName = request.DoctorName.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(doctorName))
            return BadRequest(new { message = "اسم العيادة واسم الطبيب مطلوبان" });

        var slug = request.Slug.Trim().ToLowerInvariant();
        if (slug.Length is < 2 or > 60 || !slug.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return BadRequest(new { message = "الرابط يجب أن يكون من 2 إلى 60 حرفاً: حروف إنجليزية وأرقام وشرطات فقط" });
        if (await _db.Clinics.AnyAsync(c => c.Slug == slug))
            return Conflict(new { message = "هذا الرابط مستخدم لعيادة أخرى، اختر رابطاً مختلفاً" });

        var adminUsername = request.AdminUsername.Trim().ToLowerInvariant();
        if (adminUsername.Length < 3 || !adminUsername.All(c => char.IsAsciiLetterOrDigit(c) || c == '.'))
            return BadRequest(new { message = "اسم مستخدم الأدمن يجب أن يكون 3 أحرف على الأقل من حروف وأرقام ونقاط" });
        // Usernames are unique across the whole installation: the new clinic's admin may not shadow an account of
        // any existing clinic (or of the owner), so the check looks past the owner's clinic-less scope.
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Username == adminUsername))
            return Conflict(new { message = "اسم المستخدم مستخدم بالفعل، اختر اسم مستخدم آخر" });
        var (valid, message) = PasswordPolicy.Validate(request.AdminPassword);
        if (!valid)
            return BadRequest(new { message });

        var clinic = new Domain.Clinic
        {
            Name = name,
            Slug = slug,
            DoctorName = doctorName,
            Specialty = request.Specialty.Trim(),
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
            Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim(),
            // The installation's default fees when the owner leaves the fields empty; the entity's default
            // schedule (17:00–22:00, six work days, Africa/Cairo) applies as is.
            ConsultationFee = request.ConsultationFee ?? 300m,
            FollowUpFee = request.FollowUpFee ?? 200m,
            // PLAN.md 10.2: a registered clinic starts on Basic in the free trial — its end date is the
            // per-clinic override when the owner gave one, else the installation's default trial length.
            Plan = ClinicPlan.Basic,
            SubscriptionStatus = SubscriptionStatus.Trial,
            TrialDays = request.TrialDays,
            TrialEndsAtUtc = DateTime.UtcNow.AddDays(request.TrialDays ?? _subscriptions.Value.DefaultTrialDays),
            AiEnabled = true
        };
        _db.Clinics.Add(clinic);
        await _db.SaveChangesAsync();

        var admin = new User
        {
            ClinicId = clinic.Id,
            FullName = "أدمن العيادة",
            Username = adminUsername,
            Role = UserRole.Admin,
            // PLAN_ADDENDUM.md 7.5: every created account starts with a temporary password that must be
            // changed at the first login; the login response carries the flag and the UI forces the change.
            MustChangePassword = true
        };
        admin.PasswordHash = _hasher.HashPassword(admin, request.AdminPassword);
        _db.Users.Add(admin);
        await _db.SaveChangesAsync();

        return Created($"api/owner/clinics/{clinic.Id}", new
        {
            id = clinic.Id,
            clinic = new { clinic.Id, clinic.Name, clinic.Slug, clinic.DoctorName, clinic.Specialty },
            adminUsername = admin.Username,
            adminPassword = request.AdminPassword
        });
    }

    /// <summary>
    /// The AI configuration the owner manages (PLAN.md 1.2): the appsettings defaults, the global row, and one
    /// override row per clinic. LmStudioClient picks it up through <see cref="AiSettingsService"/>.
    /// </summary>
    [HttpGet("ai-settings")]
    public async Task<IActionResult> AiSettings()
    {
        // Justified IgnoreQueryFilters: an owner token carries no clinic, so the filter would show only the global
        // row; listing every clinic's settings is exactly this panel's job.
        var rows = await _db.AiSettings.AsNoTracking().IgnoreQueryFilters().ToListAsync();
        var clinics = await _db.Clinics.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync();
        var d = _aiDefaults.Value;

        return Ok(new
        {
            defaults = new { d.BaseUrl, apiKey = d.ApiKey ?? "", model = d.ChatModel, logQuestionText = d.LogQuestionText },
            global = Shape(rows.FirstOrDefault(r => r.ClinicId == null)),
            clinics = clinics.Select(c => new { c.Id, c.Name, settings = Shape(rows.FirstOrDefault(r => r.ClinicId == c.Id)) })
        });
    }

    /// <summary>
    /// PLAN_ADDENDUM.md 7.4: the installation-wide event trail — the one screen that sees every clinic's rows,
    /// filterable by clinic and by date range. Read-only, like its clinic-admin counterpart.
    /// </summary>
    [HttpGet("audit-logs")]
    public async Task<IActionResult> AuditLogs(
        [FromQuery] int? clinicId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int? userId,
        [FromQuery] int take = 200)
    {
        var query = _db.AuditLogs.AsNoTracking();
        if (clinicId is int cid) query = query.Where(a => a.ClinicId == cid);
        if (fromUtc is DateTime from) query = query.Where(a => a.AtUtc >= from);
        if (toUtc is DateTime to) query = query.Where(a => a.AtUtc <= to);
        if (userId is int uid) query = query.Where(a => a.UserId == uid);

        var items = await query
            .OrderByDescending(a => a.AtUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(a => new { a.Id, a.ClinicId, a.AtUtc, a.UserId, a.UserName, a.Action, a.Entity, a.EntityId, a.IpAddress })
            .ToListAsync();
        return Ok(new { items });
    }

    /// <summary>
    /// PLAN_ADDENDUM.md 9.2: the installation-wide AI interaction log — every clinic's requests, filterable by
    /// clinic, the same way the event trail above is. Read-only.
    /// </summary>
    [HttpGet("audit-logs/ai")]
    public async Task<IActionResult> AiInteractions(
        [FromQuery] int? clinicId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int? userId,
        [FromQuery] int take = 200)
    {
        var query = _db.AiInteractions.AsNoTracking();
        if (clinicId is int cid) query = query.Where(a => a.ClinicId == cid);
        if (fromUtc is DateTime from) query = query.Where(a => a.AtUtc >= from);
        if (toUtc is DateTime to) query = query.Where(a => a.AtUtc <= to);
        if (userId is int uid) query = query.Where(a => a.UserId == uid);

        // Justified IgnoreQueryFilters: the owner token carries no clinic, and Users' tenant filter would hide
        // every clinic's staff names; the lookup is a same-id scalar subquery, never a cross-clinic read.
        var items = await query
            .OrderByDescending(a => a.AtUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(a => new
            {
                a.Id, a.ClinicId, a.AtUtc, a.UserId, a.PatientId, a.Kind, a.Sources, a.DurationMs, a.QuestionText, a.AnswerText,
                UserName = _db.Users.IgnoreQueryFilters().Where(u => u.Id == a.UserId).Select(u => (string?)u.FullName).FirstOrDefault()
            })
            .ToListAsync();
        return Ok(new { items });
    }

    /// <summary>Creates or updates the global row, or one clinic's row when <c>clinicId</c> is set.</summary>
    [HttpPut("ai-settings")]
    public async Task<IActionResult> SaveAiSettings([FromBody] AiSettingsPayload payload)
    {
        if (payload is null ||
            !Uri.TryCreate(payload.BaseUrl, UriKind.Absolute, out var baseUrl) ||
            baseUrl.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(payload.Model))
            return BadRequest("Base URL must be a valid http(s) address and the model must not be empty.");

        int? clinicId = payload.ClinicId;
        if (clinicId.HasValue)
        {
            var exists = await _db.Clinics.AsNoTracking().AnyAsync(c => c.Id == clinicId.Value);
            if (!exists) return NotFound();
        }

        // Justified IgnoreQueryFilters: the caller is the owner (no clinic), so the filter would hide every
        // clinic's row; the upsert is the only place a row is written and it is addressed by its own clinic id,
        // never across clinics.
        // A null clinicId must look up the global row with "IS NULL": with a null parameter, "ClinicId = @p"
        // matches nothing in SQL and a second global row would be created on every save.
        var row = clinicId.HasValue
            ? await _db.AiSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.ClinicId == clinicId)
            : await _db.AiSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.ClinicId == null);
        if (row == null)
        {
            row = new Domain.AiSettings { ClinicId = clinicId };
            _db.AiSettings.Add(row);
        }

        row.BaseUrl = baseUrl.ToString().TrimEnd('/');
        row.ApiKey = payload.ApiKey?.Trim() ?? "";
        row.Model = payload.Model.Trim();
        row.LogQuestionText = payload.LogQuestionText;   // PLAN_ADDENDUM.md 9.2
        row.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(Shape(row));
    }

    /// <summary>Removes one clinic's override so it falls back to the global row. The global row is not deletable.</summary>
    [HttpDelete("ai-settings/{clinicId:int}")]
    public async Task<IActionResult> DeleteAiSettings(int clinicId)
    {
        // Justified IgnoreQueryFilters: same reason as the upsert — the owner token has no clinic of its own.
        var row = await _db.AiSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.ClinicId == clinicId);
        if (row != null)
        {
            _db.AiSettings.Remove(row);
            await _db.SaveChangesAsync();
        }

        // Idempotent: removing a scope that has no override yet is a no-op, so the answer is 204 either way.
        return NoContent();
    }

    /// <summary>
    /// Pings the effective configuration of a scope (the global one when <c>clinicId</c> is null) by listing the
    /// models the endpoint serves, without spending a single token. Always answers 200, with an ok flag.
    /// </summary>
    [HttpPost("ai-settings/test")]
    public async Task<IActionResult> TestAiSettings([FromBody] AiSettingsPayload payload)
    {
        var options = await _settings.ResolveAsync(payload?.ClinicId, default);
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            probe.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                probe.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

            using var doc = JsonDocument.Parse(await probe.GetStringAsync("models"));
            var models = doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .ToList();
            return Ok(new { ok = true, model = options.ChatModel, models });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, model = options.ChatModel, error = "تعذّر الاتصال: " + ex.Message });
        }
    }

    /// <summary>
    /// PLAN.md 10.4 — the owner's subscription controls over one clinic: switch the plan, move the state
    /// (a fresh trial gets its end date straight away), extend the current end date, tune the trial length, or
    /// flip the AI switch. Only the fields that arrive are touched, and every change lands in the event trail
    /// through the clinic row's own audit.
    /// </summary>
    [HttpPut("clinics/{id:int}/subscription")]
    public async Task<IActionResult> UpdateSubscription(int id, [FromBody] SubscriptionUpdateRequest request)
    {
        var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.Id == id);
        if (clinic == null) return NotFound();

        if (request.Plan is string planText)
        {
            if (!Enum.TryParse<ClinicPlan>(planText, true, out var plan))
                return BadRequest(new { message = "الباقة يجب أن تكون Basic أو Pro" });
            clinic.Plan = plan;
        }

        if (request.Status is string statusText)
        {
            if (!Enum.TryParse<SubscriptionStatus>(statusText, true, out var status))
                return BadRequest(new { message = "الحالة يجب أن تكون Trial أو Active أو Suspended أو Expired" });
            var now = DateTime.UtcNow;
            clinic.SubscriptionStatus = status;
            // A fresh trial gets its end date straight away; leaving the trial clears the trial end date.
            clinic.TrialEndsAtUtc = status == SubscriptionStatus.Trial
                ? now.AddDays(clinic.TrialDays ?? _subscriptions.Value.DefaultTrialDays)
                : null;
            // An active renewal without a recorded end date starts one a year out.
            if (status == SubscriptionStatus.Active && clinic.SubscriptionEndsAtUtc is null)
                clinic.SubscriptionEndsAtUtc = now.AddDays(365);
        }

        if (request.ExtendDays is int extend)
        {
            if (extend is < 1 or > 3650)
                return BadRequest(new { message = "مدة التمديد يجب أن تكون بين يوم واحد و10 سنوات" });
            var baseDate = SubscriptionInfo.EndsAtUtc(clinic);
            var from = baseDate is DateTime b && b > DateTime.UtcNow ? b : DateTime.UtcNow;
            if (clinic.SubscriptionStatus == SubscriptionStatus.Trial)
                clinic.TrialEndsAtUtc = from.AddDays(extend);
            else
                clinic.SubscriptionEndsAtUtc = from.AddDays(extend);
        }

        if (request.TrialDays is int trialDays)
        {
            if (trialDays is < 1 or > 365)
                return BadRequest(new { message = "مدة التجربة يجب أن تكون بين يوم واحد وسنة" });
            clinic.TrialDays = trialDays;
        }

        if (request.AiEnabled is bool aiEnabled)
            clinic.AiEnabled = aiEnabled;

        await _db.SaveChangesAsync();
        return Ok(new
        {
            clinic.Plan, clinic.SubscriptionStatus, clinic.TrialEndsAtUtc, clinic.SubscriptionEndsAtUtc,
            clinic.AiEnabled, clinic.TrialDays
        });
    }

    /// <summary>
    /// PLAN.md 10.4 — books a payment against a clinic by hand: amount, its own date and a short note. The row
    /// joins the event trail as an added <c>SubscriptionPayment</c>.
    /// </summary>
    [HttpPost("clinics/{id:int}/payments")]
    public async Task<IActionResult> AddPayment(int id, [FromBody] SubscriptionPaymentRequest request)
    {
        var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.Id == id);
        if (clinic == null) return NotFound();
        if (request.Amount <= 0m)
            return BadRequest(new { message = "المبلغ يجب أن يكون أكبر من صفر" });
        if (!string.IsNullOrWhiteSpace(request.Note) && request.Note.Length > 300)
            return BadRequest(new { message = "الملاحظة يجب ألا تتجاوز 300 حرف" });

        var payment = new SubscriptionPayment
        {
            ClinicId = clinic.Id,
            Amount = request.Amount,
            PaidAtUtc = request.PaidAtUtc,
            Note = request.Note?.Trim(),
            CreatedByUserName = "مالك النظام"
        };
        _db.SubscriptionPayments.Add(payment);
        await _db.SaveChangesAsync();

        return Created($"api/owner/clinics/{clinic.Id}/payments/{payment.Id}", new
        {
            payment.Id, payment.Amount, payment.PaidAtUtc, payment.Note
        });
    }

    private static object? Shape(Domain.AiSettings? row) => row == null
        ? null
        : new { row.Id, row.ClinicId, row.BaseUrl, row.ApiKey, row.Model, row.LogQuestionText, row.UpdatedAtUtc };

    /// <summary>
    /// The per-clinic numbers behind both endpoints. Every total is a plain count, and money is added up in memory:
    /// SQLite cannot aggregate a decimal column (the same reason DashboardController sums its invoices client side).
    ///
    /// Justified IgnoreQueryFilters: an owner token carries no clinic, and the query filters compare every
    /// clinic-owned row with the caller's clinic — null here, so the filtered sets are always empty. Reading across
    /// clinics is exactly this endpoint's job; the tenant context still cannot reach a single clinic's data through
    /// any clinic-scoped endpoint, because those refuse the owner role outright.
    /// </summary>
    private async Task<List<object>> SummaryAsync(List<Domain.Clinic> clinics)
    {
        var staff = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.ClinicId != null && !u.IsDeleted) // PLAN.md 10.6: deleted accounts are not live staff
            .GroupBy(u => u.ClinicId)
            .Select(g => new { ClinicId = g.Key, Total = g.Count(), Active = g.Count(u => u.IsActive) })
            .ToListAsync();

        // Logically deleted files (PLAN_ADDENDUM.md 9.3) are not live patients, so the owner's per-clinic counts
        // skip them the way every clinic-facing query does.
        var patients = await _db.Patients.IgnoreQueryFilters().AsNoTracking()
            .Where(p => !p.IsDeleted)
            .GroupBy(p => p.ClinicId)
            .Select(g => new { ClinicId = g.Key, Count = g.Count() })
            .ToListAsync();

        var visits = await _db.Visits.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(v => v.ClinicId)
            .Select(g => new { ClinicId = g.Key, Count = g.Count(), LastAt = g.Max(v => (DateTime?)v.VisitDate) })
            .ToListAsync();

        var appointments = await _db.Appointments.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(a => a.ClinicId)
            .Select(g => new { ClinicId = g.Key, Count = g.Count() })
            .ToListAsync();

        var invoices = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(i => i.ClinicId)
            .Select(g => new { ClinicId = g.Key, Count = g.Count() })
            .ToListAsync();

        var result = new List<object>(clinics.Count);
        foreach (var clinic in clinics)
        {
            var revenue = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.ClinicId == clinic.Id)
                .Select(i => i.Paid)
                .ToListAsync();

            result.Add(new
            {
                clinic.Id, clinic.Name, clinic.Slug, clinic.DoctorName, clinic.Specialty,
                clinic.Phone, clinic.Address, clinic.CreatedAtUtc, clinic.OnlineBookingEnabled,
                staff = new
                {
                    total = staff.FirstOrDefault(s => s.ClinicId == clinic.Id)?.Total ?? 0,
                    active = staff.FirstOrDefault(s => s.ClinicId == clinic.Id)?.Active ?? 0
                },
                patients = patients.FirstOrDefault(p => p.ClinicId == clinic.Id)?.Count ?? 0,
                visits = visits.FirstOrDefault(v => v.ClinicId == clinic.Id)?.Count ?? 0,
                lastVisitAt = visits.FirstOrDefault(v => v.ClinicId == clinic.Id)?.LastAt,
                appointments = appointments.FirstOrDefault(a => a.ClinicId == clinic.Id)?.Count ?? 0,
                invoices = invoices.FirstOrDefault(i => i.ClinicId == clinic.Id)?.Count ?? 0,
                revenue = revenue.Sum(),
                // PLAN.md 10.5: the plan, the state and the effective end date behind the "expiring" filter.
                plan = clinic.Plan.ToString(),
                subscriptionStatus = clinic.SubscriptionStatus.ToString(),
                endsAtUtc = SubscriptionInfo.EndsAtUtc(clinic),
                daysRemaining = SubscriptionInfo.DaysRemaining(clinic)
            });
        }

        return result;
    }
    [HttpPost("upload-catalog")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> UploadCatalogPdf(IFormFile file, [FromQuery] string type)
    {
        if (!Enum.TryParse<CatalogItemType>(type, true, out var itemType))
            return BadRequest("Invalid type. Use 'Lab' or 'Radiology'.");

        if (file == null || file.Length == 0)
            return BadRequest("Please select a file.");

        var fileName = file.FileName;
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Only PDF files are allowed.");

        List<string> rawLines;
        try
        {
            using var stream = file.OpenReadStream();
            using var pdf = PdfDocument.Open(stream);
            var text = "";
            foreach (var page in pdf.GetPages()) text += page.Text + "\n";
            rawLines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch
        {
            return BadRequest("فشل قراءة ملف PDF");
        }
        var cleanedLines = new List<string>();
        foreach (var line in rawLines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 3) continue;
            if (Regex.IsMatch(trimmed, @"^[\d\.\-\$\€\£\¥\s]+$")) continue;
            cleanedLines.Add(trimmed);
        }

        return Ok(cleanedLines);
    }

    [HttpPost("save-catalog")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> SaveCatalog([FromBody] SaveCatalogRequest request)
    {
        if (request == null || request.Lines == null || !request.Lines.Any())
            return BadRequest("No lines provided.");

        if (!Enum.TryParse<CatalogItemType>(request.Type, true, out var itemType))
            return BadRequest("Invalid type. Use 'Lab' or 'Radiology'.");

        var items = request.Lines.Select(line => new CatalogItem
        {
            Name = line.Trim(),
            Type = itemType,
            ClinicId = 0
        }).ToList();

        _db.CatalogItems.AddRange(items);
        await _db.SaveChangesAsync();

        return Ok(new { Count = items.Count });
    }
    [HttpGet("catalog/items")]
    public async Task<IActionResult> GetCatalogItems([FromQuery] string? type, [FromQuery] string? q)
    {
        var query = _db.CatalogItems.AsNoTracking().Where(ci => ci.ClinicId == 0);
        if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<CatalogItemType>(type, true, out var itemType))
        {
            query = query.Where(ci => ci.Type == itemType);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var lower = q.Trim().ToLower();
            query = query.Where(ci => ci.Name.ToLower().Contains(lower));
        }
        var items = await query.Select(ci => new { ci.Id, ci.Name }).OrderBy(ci => ci.Name).ToListAsync();
        return Ok(items);
    }

    [HttpGet("catalog/drugs")]
    public async Task<IActionResult> GetDrugCatalog([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Ok(Array.Empty<object>());
        }
        var lower = q.Trim().ToLower();
        var startsWith = await _db.DrugCatalogs.AsNoTracking()
            .Where(d => d.TradeName.ToLower().StartsWith(lower) || d.ActiveIngredient.ToLower().StartsWith(lower))
            .Select(d => new { d.Id, Name = d.TradeName })
            .Take(15)
            .ToListAsync();
        if (startsWith.Count >= 15)
        {
            return Ok(startsWith);
        }
        var remaining = 15 - startsWith.Count;
        var contains = await _db.DrugCatalogs.AsNoTracking()
            .Where(d => !(d.TradeName.ToLower().StartsWith(lower) || d.ActiveIngredient.ToLower().StartsWith(lower))
                        && (d.TradeName.ToLower().Contains(lower) || d.ActiveIngredient.ToLower().Contains(lower)))
            .Select(d => new { d.Id, Name = d.TradeName })
            .Take(remaining)
            .ToListAsync();
        var result = startsWith.Concat(contains).ToList();
        return Ok(result);
    }

    public record SaveCatalogRequest(List<string> Lines, string Type);
}

/// <summary>The body of PUT api/owner/ai-settings and POST api/owner/ai-settings/test (PLAN.md 1.2).</summary>
public record AiSettingsPayload(int? ClinicId, string BaseUrl = "", string? ApiKey = null, string Model = "", bool LogQuestionText = false);

/// <summary>
/// The body of POST api/owner/clinics (PLAN.md 10.1). The fees fall back to the installation defaults (300/200)
/// when left null; the schedule uses the entity's defaults until the clinic admin tunes it.
/// </summary>
public record NewClinicRequest(string Name, string Slug, string DoctorName, string Specialty,
    string? Phone, string? Address, string AdminUsername, string AdminPassword,
    decimal? ConsultationFee = null, decimal? FollowUpFee = null, int? TrialDays = null);

/// <summary>
/// The body of PUT api/owner/clinics/{id}/subscription (PLAN.md 10.4). Every field is optional and only the
/// fields that arrive are touched, so the panel can send one control at a time.
/// </summary>
public record SubscriptionUpdateRequest(string? Plan, string? Status, int? ExtendDays, int? TrialDays, bool? AiEnabled);

/// <summary>The body of POST api/owner/clinics/{id}/payments (PLAN.md 10.4): a manual payment record.</summary>
public record SubscriptionPaymentRequest(decimal Amount, DateTime PaidAtUtc, string? Note);
