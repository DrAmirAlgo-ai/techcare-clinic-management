using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;

namespace TechCare.Clinic.Api.Infrastructure;

/// <summary>
/// PLAN.md 10.3 — the subscription check that turns a suspended or expired clinic into a read-only one, the way
/// UserSessionValidator turns a disabled account into a 401 (PLAN_ADDENDUM.md 7.1): one cached decision per
/// clinic for 30 seconds, so the cost of the check on every request is zero.
///
/// "Read-only" means GETs keep working (the doctor still reads files and prints), while every write is refused
/// with the Arabic explanation — the answer the UI shows instead of a silent failure.
/// </summary>
public class SubscriptionGuard
{
    /// <summary>How long one decision may be reused, the same window UserSessionValidator uses.</summary>
    public const int CacheSeconds = 30;

    private readonly IServiceScopeFactory _scopes;
    private static readonly ConcurrentDictionary<int, (bool Locked, DateTime CachedAtUtc)> _cache = new();

    public SubscriptionGuard(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <summary>Tests call this right after they change a clinic's subscription row, so the next request re-checks.</summary>
    public static void ClearCache() => _cache.Clear();

    /// <summary>True when the caller's clinic may not write right now. Owner tokens (no clinic) never lock.</summary>
    public Task<bool> IsLockedAsync(ClaimsPrincipal principal)
    {
        if (!int.TryParse(principal.FindFirstValue(ClaimNames.ClinicId), out var clinicId))
            return Task.FromResult(false);
        return IsLockedAsync(clinicId);
    }

    public async Task<bool> IsLockedAsync(int clinicId)
    {
        if (_cache.TryGetValue(clinicId, out var cached)
            && (DateTime.UtcNow - cached.CachedAtUtc).TotalSeconds < CacheSeconds)
            return cached.Locked;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        // Justified IgnoreQueryFilters: this runs during request authentication, before any clinic is stamped on
        // the tenant context, so the filter would find nothing here.
        var clinic = await db.Clinics.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clinicId);
        var locked = clinic != null && IsLocked(clinic, DateTime.UtcNow);
        _cache[clinicId] = (locked, DateTime.UtcNow);
        return locked;
    }

    /// <summary>
    /// The rule in one place: suspended and marked-expired always lock; trial and active lock the moment their
    /// end date passes. An active clinic with no recorded end date never locks.
    /// </summary>
    public static bool IsLocked(Domain.Clinic clinic, DateTime nowUtc) =>
        clinic.SubscriptionStatus is Domain.SubscriptionStatus.Suspended or Domain.SubscriptionStatus.Expired
        || (clinic.SubscriptionStatus == Domain.SubscriptionStatus.Trial
            && clinic.TrialEndsAtUtc is DateTime trialEnd && trialEnd <= nowUtc)
        || (clinic.SubscriptionStatus == Domain.SubscriptionStatus.Active
            && clinic.SubscriptionEndsAtUtc is DateTime end && end <= nowUtc);
}

/// <summary>
/// PLAN.md 10.2 — the shared shape of the subscription numbers the API answers carry: which end date is the one
/// that matters, and how many whole days are left until it.
/// </summary>
public static class SubscriptionInfo
{
    /// <summary>A trial ends on its trial date; every other state ends on the renewal date.</summary>
    public static DateTime? EndsAtUtc(Domain.Clinic clinic) =>
        clinic.SubscriptionStatus == Domain.SubscriptionStatus.Trial
            ? clinic.TrialEndsAtUtc
            : clinic.SubscriptionEndsAtUtc;

    /// <summary>Whole days until that end date (negative once it has passed); null when there is no end date.</summary>
    public static int? DaysRemaining(Domain.Clinic clinic)
    {
        var end = EndsAtUtc(clinic);
        return end is DateTime e ? (int)Math.Ceiling((e - DateTime.UtcNow).TotalDays) : null;
    }
}
