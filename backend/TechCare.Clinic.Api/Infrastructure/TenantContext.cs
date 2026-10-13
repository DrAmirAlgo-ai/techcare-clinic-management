using System.Security.Claims;
using TechCare.Clinic.Api.Domain;

namespace TechCare.Clinic.Api.Infrastructure;

/// <summary>
/// The clinic and user the current request acts on behalf of.
/// Staff requests get it from the JWT; public requests set it after resolving the clinic slug.
/// When ClinicId is null, clinic-owned queries return nothing (fail closed).
/// </summary>
public class TenantContext
{
    public int? ClinicId { get; private set; }
    public int? UserId { get; private set; }
    public UserRole? Role { get; private set; }

    /// <summary>
    /// PLAN_ADDENDUM.md 7.4: the account's name (the JWT's Name claim) and the request's IP. Both are stamped on
    /// the audit rows (ClinicDbContext), so the trail answers "who, from where" without an extra query.
    /// </summary>
    public string? UserName { get; private set; }
    public string? IpAddress { get; private set; }

    public void SetFromPrincipal(ClaimsPrincipal principal)
    {
        if (int.TryParse(principal.FindFirstValue(ClaimNames.ClinicId), out var clinicId))
            ClinicId = clinicId;
        if (int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            UserId = userId;
        if (Enum.TryParse<UserRole>(principal.FindFirstValue(ClaimTypes.Role), out var role))
            Role = role;
        UserName = principal.FindFirstValue(ClaimTypes.Name);
    }

    public void SetClinic(int clinicId) => ClinicId = clinicId;

    /// <summary>PLAN_ADDENDUM.md 7.4: stamped by the tenant middleware on every request, request IP included.</summary>
    public void SetIp(string? ipAddress) => IpAddress = ipAddress;

    public int RequireClinicId() =>
        ClinicId ?? throw new InvalidOperationException("No clinic in the current request context.");

    public int RequireUserId() =>
        UserId ?? throw new InvalidOperationException("No user in the current request context.");
}

public static class ClaimNames
{
    public const string ClinicId = "clinicId";

    /// <summary>
    /// The <see cref="Domain.User.TokenVersion"/> as it was at login time. Re-checked against the database on
    /// every authenticated request (PLAN_ADDENDUM.md 7.1).
    /// </summary>
    public const string TokenVersion = "TokenVersion";
}

public class TenantMiddleware
{
    private readonly RequestDelegate _next;

    public TenantMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        if (context.User.Identity?.IsAuthenticated == true)
            tenant.SetFromPrincipal(context.User);
        tenant.SetIp(context.Connection.RemoteIpAddress?.ToString());

        await _next(context);
    }
}
