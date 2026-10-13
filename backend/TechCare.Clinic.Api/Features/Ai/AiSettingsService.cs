using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Ai;

/// <summary>
/// The effective AI configuration for a request (PLAN.md 1.2). appsettings stays the installation default; the
/// owner's global row (a row with no clinic) overrides it, and the calling clinic's own row overrides that. The
/// query is confined to "the global row plus this clinic's row", so a row of another clinic can never take effect.
/// </summary>
public class AiSettingsService
{
    private readonly ClinicDbContext _db;
    private readonly TenantContext _tenant;
    private readonly IOptions<AiOptions> _defaults;

    public AiSettingsService(ClinicDbContext db, TenantContext tenant, IOptions<AiOptions> defaults)
    {
        _db = db;
        _tenant = tenant;
        _defaults = defaults;
    }

    /// <summary>Effective options for the clinic of the current request.</summary>
    public Task<AiOptions> ResolveForCurrentTenantAsync(CancellationToken ct = default) =>
        ResolveAsync(_tenant.ClinicId, ct);

    /// <summary>
    /// Effective options for one clinic: the appsettings defaults, overridden by the global row, then by that
    /// clinic's own row when it has one.
    ///
    /// Justified IgnoreQueryFilters: the query filter confines a tenant to its own row, but the effective
    /// configuration must always be able to fall back to the global row, which belongs to no clinic — and the
    /// owner's test ping (no clinic at all) must still reach the global row. The WHERE clause lets at most two
    /// rows through: the global one and the requested clinic's. No other clinic's settings can reach the caller.
    /// </summary>
    public async Task<AiOptions> ResolveAsync(int? clinicId, CancellationToken ct = default)
    {
        var rows = await _db.AiSettings.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.ClinicId == null || s.ClinicId == clinicId)
            .OrderBy(s => s.ClinicId == null ? 0 : 1)
            .ToListAsync(ct);

        var options = _defaults.Value.Clone();
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.BaseUrl))
                options.BaseUrl = row.BaseUrl.Trim();
            // An empty key means "no key" (the local server needs none) and overrides the default on purpose.
            options.ApiKey = string.IsNullOrWhiteSpace(row.ApiKey) ? null : row.ApiKey.Trim();
            if (!string.IsNullOrWhiteSpace(row.Model))
                options.ChatModel = row.Model.Trim();
            // A stored flag is always a deliberate choice, so it overrides in the same global-then-clinic order
            // (PLAN_ADDENDUM.md 9.2: a clinic may keep its log text-free even when the global row allows text).
            options.LogQuestionText = row.LogQuestionText;
        }

        return options;
    }
}
