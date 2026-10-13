using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Auth;

/// <summary>PLAN_ADDENDUM.md 7.2: the clinic code is optional; it becomes required when the username exists at more than one clinic.</summary>
public record LoginRequest(string Username, string Password, string? ClinicSlug = null);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    /// <summary>PLAN_ADDENDUM.md 7.5: failed attempts before the account is locked.</summary>
    private const int MaxFailedLogins = 5;

    /// <summary>PLAN_ADDENDUM.md 7.5: how long a locked account stays out.</summary>
    private const int LockMinutes = 15;

    private readonly ClinicDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly IPasswordHasher<User> _hasher;
    private readonly TenantContext _tenant;

    public AuthController(ClinicDbContext db, JwtTokenService jwt, IPasswordHasher<User> hasher, TenantContext tenant)
    {
        _db = db;
        _jwt = jwt;
        _hasher = hasher;
        _tenant = tenant;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var username = request.Username?.Trim().ToLowerInvariant() ?? "";
        var slug = request.ClinicSlug?.Trim();

        // Justified IgnoreQueryFilters: login happens before any clinic is known, so the account is looked up across
        // every clinic. The clinic taken from the found user is what scopes the rest of the request. The SuperAdmin
        // owner (PLAN.md 1.1) has no clinic at all, so its requests stay clinic-less and fail closed everywhere
        // except api/owner.
        // A logically deleted account (PLAN.md 10.6) can never sign in: the cross-clinic lookup above skips the
        // tenant filter, so the delete flag is excluded explicitly here.
        var users = await _db.Users.IgnoreQueryFilters().Where(u => u.Username == username && !u.IsDeleted).ToListAsync();

        // PLAN_ADDENDUM.md 7.2: usernames are unique per clinic now, so the same name may exist at several clinics.
        // With a clinic code the search is confined to that clinic (an unknown code matches nothing, so a valid
        // username at a mistyped clinic reads as "wrong credentials" rather than "clinic does not exist"); without
        // one, the login may only proceed when the name is unambiguous — otherwise the caller must say which
        // clinic it means.
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var slugClinic = await _db.Clinics.FirstOrDefaultAsync(c => c.Slug.ToLower() == slug.ToLower());
            users = slugClinic == null ? new List<User>() : users.Where(u => u.ClinicId == slugClinic.Id).ToList();
        }
        else if (users.Count > 1)
        {
            // Effectively unreachable now: usernames are unique across the whole installation (the controllers and
            // the unique index prevent duplicates), so a name can never belong to two clinics. Kept as the last
            // line of defence in case a row ever slips through both.
            return Conflict(new { message = "اسم المستخدم مشترك بين أكثر من عيادة، اكتب كود العيادة" });
        }

        if (users.Count == 0)
            return Unauthorized(new { message = "اسم المستخدم أو كلمة المرور غير صحيحة" });

        var user = users[0];

        // PLAN_ADDENDUM.md 7.5: a locked account refuses even the correct password until the window passes.
        if (user.LockedUntilUtc is DateTime until && until > DateTime.UtcNow)
            return Unauthorized(new { message = "الحساب مقفل مؤقتا بعد محاولات دخول فاشلة، حاول بعد 15 دقيقة" });

        var passwordOk = user.IsActive
            && _hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? "") != PasswordVerificationResult.Failed;

        if (!passwordOk)
        {
            // PLAN_ADDENDUM.md 7.5: count the failures per account (the IP rate limit above still stands in front of
            // it); the fifth one locks the account for 15 minutes and resets the counter.
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedLogins)
            {
                user.FailedLoginCount = 0;
                user.LockedUntilUtc = DateTime.UtcNow.AddMinutes(LockMinutes);
            }
            await _db.SaveChangesAsync();
            return Unauthorized(new { message = "اسم المستخدم أو كلمة المرور غير صحيحة" });
        }

        user.FailedLoginCount = 0;
        user.LockedUntilUtc = null;

        var clinic = user.ClinicId is int clinicId ? await _db.Clinics.FirstAsync(c => c.Id == clinicId) : null;
        var (token, expires) = _jwt.CreateToken(user);

        if (user.ClinicId is int ownedClinicId)
            _tenant.SetClinic(ownedClinicId);

        // PLAN_ADDENDUM.md 7.4: a successful login joins the trail — who, when, from which IP. No password, no data.
        _db.LogAction("Login", "User", user.Id, userId: user.Id, userName: user.Username, clinicId: user.ClinicId);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            token,
            expiresAtUtc = expires,
            mustChangePassword = user.MustChangePassword, // PLAN_ADDENDUM.md 7.5: the UI forces the change at first login
            user = new { user.Id, user.FullName, user.Username, Role = user.Role.ToString(),
                // PLAN.md 1.4: the effective set (explicit choices, else role defaults) — the UI gates its pages from it.
                Permissions = Permissions.Effective(user) },
            clinic = clinic == null ? null : ClinicView(clinic)
        });
    }
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == _tenant.UserId);
        if (user == null || !user.IsActive) return Unauthorized();
        var clinic = user.ClinicId is int clinicId ? await _db.Clinics.FirstAsync(c => c.Id == clinicId) : null;
        return Ok(new
        {
            user = new { user.Id, user.FullName, user.Username, Role = user.Role.ToString(),
                Permissions = Permissions.Effective(user) }, // PLAN.md 1.4
            mustChangePassword = user.MustChangePassword,
            clinic = clinic == null ? null : ClinicView(clinic)
        });
    }

    /// <summary>
    /// PLAN.md 10.2/10.3: the session's view of the caller's clinic — its identity plus the subscription numbers
    /// the layout's bar shows (plan, state, days left, the AI switch). The owner belongs to no clinic, so null.
    /// </summary>
    private static object ClinicView(Domain.Clinic clinic) => new
    {
        clinic.Id, clinic.Name, clinic.Slug, clinic.DoctorName,
        // PLAN.md 10.7: what the sidebar shows — the clinic's own name/logo instead of the platform default.
        displayName = clinic.DisplayName ?? clinic.Name,
        hasLogo = clinic.LogoPath != null,
        plan = clinic.Plan.ToString(),
        subscriptionStatus = clinic.SubscriptionStatus.ToString(),
        daysRemaining = SubscriptionInfo.DaysRemaining(clinic),
        aiEnabled = clinic.AiEnabled
    };

    /// <summary>
    /// PLAN_ADDENDUM.md 7.5: the account's own password change — the forced first-login change and the owner of
    /// the account any time. Bumps <see cref="User.TokenVersion"/>, so every token issued with the old password
    /// stops working; the client signs in again and receives a fresh one.
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == _tenant.UserId);
        if (user == null || !user.IsActive) return Unauthorized();

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
            return BadRequest(new { message = "كلمة المرور الحالية غير صحيحة" });

        var (valid, message) = PasswordPolicy.Validate(request.NewPassword);
        if (!valid) return BadRequest(new { message });
        if (string.Equals(request.NewPassword.Trim(), request.CurrentPassword.Trim(), StringComparison.Ordinal))
            return BadRequest(new { message = "كلمة المرور الجديدة يجب أن تختلف عن الحالية" });

        user.PasswordHash = _hasher.HashPassword(user, request.NewPassword.Trim());
        user.MustChangePassword = false;
        user.TokenVersion++;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
