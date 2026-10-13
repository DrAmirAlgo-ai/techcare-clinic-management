using TechCare.Clinic.Api.Domain;

namespace TechCare.Clinic.Api.Infrastructure;

/// <summary>
/// PLAN.md 1.4 — the per-page, per-action permission catalog. These keys are what an account may or may not do,
/// and they replace the three fixed role sets as the thing the clinic endpoints check: a role no longer decides
/// access directly, it only supplies the default set for an account the admin has not customised yet.
///
/// Each key names a page and the actions that page performs (the queue key covers checking patients in and moving
/// them through the day; the admin key covers the management endpoints: users, clinic settings, deleting files).
/// The owner (SuperAdmin, PLAN.md 1.1) gets no key at all — the owner reaches only api/owner through its role.
/// </summary>
public static class Permissions
{
    public const string Dashboard = "dashboard";
    public const string Queue = "queue";
    public const string Appointments = "appointments";
    public const string Patients = "patients";
    public const string Invoices = "invoices";
    public const string Assistant = "assistant";
    public const string Admin = "admin";

    /// <summary>Every known key, in display order. The settings UI and the validation endpoints work from this list.</summary>
    public static readonly string[] All =
        [Dashboard, Queue, Appointments, Patients, Invoices, Assistant, Admin];

    /// <summary>
    /// Backward compatibility: what the three fixed roles (Infrastructure\Roles.cs) used to grant. An account
    /// without an explicit set keeps behaving exactly as its role always did.
    /// </summary>
    public static string[] DefaultSet(UserRole role) => role switch
    {
        UserRole.Reception => [Dashboard, Queue, Appointments, Patients, Invoices],
        UserRole.Doctor => [Dashboard, Queue, Appointments, Patients, Invoices, Assistant],
        UserRole.Admin => [Dashboard, Queue, Appointments, Patients, Invoices, Assistant, Admin],
        _ => []
    };

    /// <summary>
    /// The set that actually governs the account: the admin's explicit choices when there are any, otherwise the
    /// role's defaults. A stored empty string is a deliberate "nothing" — explicit, not "unset". Unknown keys in
    /// the stored text are dropped, so a future schema can only add powers, never keep a stale one.
    /// </summary>
    public static string[] Effective(UserRole role, string? stored)
    {
        if (stored is null)
            return DefaultSet(role);
        var kept = stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        return All.Where(kept.Contains).ToArray();
    }

    /// <summary>The account's effective set: its explicit choices first, its role's defaults otherwise.</summary>
    public static string[] Effective(User user) => Effective(user.Role, user.Permissions);

    /// <summary>
    /// Which roles carry <paramref name="key"/> by default. The role-matrix test (PLAN_ADDENDUM.md 0.3) reads a
    /// policy-bearing endpoint through this, so its table can stay in terms of the fixture accounts' roles.
    /// </summary>
    public static string[] DefaultRoles(string key)
    {
        var roles = new List<string>(3);
        foreach (var role in new[] { UserRole.Admin, UserRole.Doctor, UserRole.Reception })
            if (DefaultSet(role).Contains(key))
                roles.Add(role.ToString());
        return [.. roles];
    }
}
