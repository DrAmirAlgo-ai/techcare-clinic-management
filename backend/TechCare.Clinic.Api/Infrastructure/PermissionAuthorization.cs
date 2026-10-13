using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace TechCare.Clinic.Api.Infrastructure;

/// <summary>
/// PLAN.md 1.4: one named policy per permission key (registered in Program.cs). The requirement is satisfied when
/// the signed-in account's effective permission set — the admin's explicit choices, or the role's defaults when
/// none were made — contains the key. The owner (SuperAdmin) holds no keys, so every policy fails closed for it.
/// </summary>
public sealed record PermissionRequirement(string Key) : IAuthorizationRequirement;

public class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly UserSessionValidator _validator;

    public PermissionHandler(UserSessionValidator validator) => _validator = validator;

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return;

        // The validator caches the account's row for 30 seconds, so a permission the admin changes applies within
        // that window without needing to reissue tokens; the token itself stays role-scoped (PLAN_ADDENDUM.md 7.1).
        var set = await _validator.EffectivePermissionsAsync(userId);
        if (set is { Count: > 0 } && set.Contains(requirement.Key))
            context.Succeed(requirement);
    }
}
