using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using static TechCare.Clinic.Api.Infrastructure.ClaimNames;

namespace TechCare.Clinic.Api.Infrastructure;

/// <summary>
/// The single point where a signed-in account is re-checked against the database after the JWT has been
/// validated (PLAN_ADDENDUM.md 7.1): the account must still exist, still be active, and its TokenVersion
/// and Role must match what the token claims. Without this, a disabled user (or a user whose password or
/// role changed) keeps working until the token expires; with it, every such change takes effect within the
/// cache window.
///
/// Each user's checked state is kept in memory for <see cref="CacheSeconds"/> seconds, so ordinary
/// requests cost nothing. The cache row also carries the account's explicit permission set (PLAN.md 1.4),
/// which <c>PermissionHandler</c> reads through <see cref="EffectivePermissionsAsync"/> — one check point,
/// and a permission change applies within the same window as a deactivation.
/// </summary>
public class UserSessionValidator
{
    /// <summary>How long a checked state stays trusted; deactivation therefore applies within 30 seconds.</summary>
    public const int CacheSeconds = 30;

    private readonly IServiceScopeFactory _scopes;
    private static readonly ConcurrentDictionary<int, Entry> _cache = new();

    private readonly record struct Entry(
        bool Exists, bool IsActive, int TokenVersion, UserRole Role, string? Permissions, DateTime CachedAtUtc);

    public UserSessionValidator(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <summary>For the test host: forget every checked state so the next request reads the database.</summary>
    public static void ClearCache() => _cache.Clear();

    public async Task<bool> IsStillValidAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (!int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return false;

        return Matches(await LoadEntryAsync(userId, ct), principal);
    }

    /// <summary>
    /// PLAN.md 1.4: the account's effective permission set — the admin's explicit choices when any were made,
    /// otherwise the role's defaults — read from the same cached row the validity check uses, so it costs no
    /// extra query. Null when the account no longer exists.
    /// </summary>
    public async Task<HashSet<string>?> EffectivePermissionsAsync(int userId, CancellationToken ct = default)
    {
        var entry = await LoadEntryAsync(userId, ct);
        return entry.Exists ? new HashSet<string>(Permissions.Effective(entry.Role, entry.Permissions)) : null;
    }

    private async Task<Entry> LoadEntryAsync(int userId, CancellationToken ct)
    {
        if (_cache.TryGetValue(userId, out var cached) && (DateTime.UtcNow - cached.CachedAtUtc).TotalSeconds < CacheSeconds)
            return cached;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        // Justified IgnoreQueryFilters: authentication runs before TenantMiddleware has stamped any clinic,
        // and the owner account (no clinic) must be checkable too. The id comes from a signed token, not the
        // request body, so no clinic's data is exposed.
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);

        var entry = user == null
            ? new Entry(false, false, 0, default, null, DateTime.UtcNow)
            : new Entry(true, user.IsActive, user.TokenVersion, user.Role, user.Permissions, DateTime.UtcNow);
        _cache[userId] = entry;

        return entry;
    }

    private static bool Matches(Entry entry, ClaimsPrincipal principal) =>
        entry.Exists
        && entry.IsActive
        && entry.TokenVersion == (int.TryParse(principal.FindFirstValue(ClaimNames.TokenVersion), out int parsedVersion) ? parsedVersion : -1)
        && entry.Role.ToString() == principal.FindFirstValue(ClaimTypes.Role);
}
