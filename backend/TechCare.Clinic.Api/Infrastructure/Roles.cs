namespace TechCare.Clinic.Api.Infrastructure;

/// <summary>
/// Role constants. Since PLAN.md 1.4 the clinic endpoints check the per-account permission sets
/// (Infrastructure\Permissions.cs) instead of these role sets: every permission key maps back to exactly the
/// roles that hold it by default, so an account the admin never customised behaves as its role always did.
/// <see cref="SuperAdmin"/> stays a plain role check — the owner's panel (api/owner) is the only surface it opens.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Staff = "Admin,Doctor,Reception";
    public const string Clinical = "Admin,Doctor";

    /// <summary>
    /// The installation owner (PLAN.md 1.1). Absent from the clinic sets above on purpose: an owner token carries
    /// no clinic, so it must never be accepted by a clinic-scoped endpoint (every one of them would fail closed).
    /// </summary>
    public const string SuperAdmin = nameof(Domain.UserRole.SuperAdmin);
}
