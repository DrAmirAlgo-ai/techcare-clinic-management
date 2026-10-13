using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Users;

public record CreateUserRequest(string FullName, string Username, string Password, UserRole Role);
public record UpdateUserRequest(string FullName, UserRole Role, bool IsActive);
public record ResetPasswordRequest(string NewPassword);

/// <summary>
/// PLAN.md 1.4: a null <see cref="Permissions"/> restores the role's defaults; anything else — even an empty
/// array — is a deliberate explicit set.
/// </summary>
public record SetUserPermissionsRequest(string[]? Permissions);

[ApiController]
[Route("api/users")]
[Authorize(Policy = Permissions.Admin)]
public class UsersController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly TenantContext _tenant;

    public UsersController(ClinicDbContext db, IPasswordHasher<User> hasher, TenantContext tenant)
    {
        _db = db;
        _hasher = hasher;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var users = await _db.Users.OrderBy(u => u.Role).ThenBy(u => u.FullName).ToListAsync();
        return Ok(users.Select(u => new
        {
            u.Id, u.FullName, u.Username, Role = u.Role.ToString(), u.IsActive, u.CreatedAtUtc,
            // PLAN.md 1.4: the settings screen shows whether the account carries custom permissions.
            IsExplicit = u.Permissions != null,
            Permissions = Permissions.Effective(u)
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest request)
    {
        var username = request.Username.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(request.FullName) || username.Length < 3)
            return BadRequest(new { message = "الاسم واسم المستخدم (3 أحرف على الأقل) مطلوبان" });
        var (valid, message) = PasswordPolicy.Validate(request.Password);
        if (!valid)
            return BadRequest(new { message });
        // The SuperAdmin owner (PLAN.md 1.1) is not part of any clinic, so no clinic admin can create one: such an
        // account carries no clinic and could only ever reach api/owner, never this clinic's data.
        if (request.Role == UserRole.SuperAdmin)
            return BadRequest(new { message = "لا يمكن إنشاء حساب مالك النظام من لوحة العيادة" });
        // Usernames are unique across the whole installation (7.2 reversed by product decision): the name may be
        // taken by another clinic's account or by the owner, so the check looks past this clinic's filter and the
        // unique index stays the last line of defence against a lost race.
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Username == username))
            return Conflict(new { message = "اسم المستخدم مسجل مسبقاً، اختر اسماً مختلفاً" });

        var user = new User
        {
            ClinicId = _tenant.RequireClinicId(),
            FullName = request.FullName.Trim(),
            Username = username,
            Role = request.Role,
            // PLAN_ADDENDUM.md 7.5: admin-created accounts start with a temporary password that the user must
            // change at the first login.
            MustChangePassword = true
        };
        user.PasswordHash = _hasher.HashPassword(user, request.Password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return Ok(new { user.Id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateUserRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();

        // Same rule as Create: the owner role stays out of the clinic panel (PLAN.md 1.1).
        if (request.Role == UserRole.SuperAdmin)
            return BadRequest(new { message = "لا يمكن منح صلاحية مالك النظام من لوحة العيادة" });

        if (user.Id == _tenant.UserId && (!request.IsActive || request.Role != UserRole.Admin))
            return BadRequest(new { message = "لا يمكن إيقاف حسابك أو إزالة صلاحية المدير عن نفسك" });

        user.FullName = request.FullName.Trim();
        var securityChanged = user.Role != request.Role || user.IsActive != request.IsActive;
        if (user.Role != request.Role)
            // PLAN.md 1.4: a new role starts from that role's defaults; the admin customises the set again after.
            user.Permissions = null;
        user.Role = request.Role;
        user.IsActive = request.IsActive;
        // A role change or deactivation must void every token issued before it (PLAN_ADDENDUM.md 7.1):
        // the token's TokenVersion claim stops matching the row on the next request.
        if (securityChanged)
            user.TokenVersion++;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// PLAN.md 1.4: the account's permission set for the settings screen — its role's defaults alongside what is
    /// in force, so the UI can show which of the two is governing.
    /// </summary>
    [HttpGet("{id:int}/permissions")]
    public async Task<IActionResult> GetPermissions(int id)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        return Ok(new
        {
            Role = user.Role.ToString(),
            IsExplicit = user.Permissions != null,
            Effective = Permissions.Effective(user),
            Defaults = Permissions.DefaultSet(user.Role),
            All = Permissions.All
        });
    }

    [HttpPut("{id:int}/permissions")]
    public async Task<IActionResult> SetPermissions(int id, SetUserPermissionsRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();

        var keys = request.Permissions?
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct()
            .ToArray() ?? Array.Empty<string>();
        var unknown = keys.Where(k => !Permissions.All.Contains(k)).ToArray();
        if (unknown.Length > 0)
            return BadRequest(new { message = $"صلاحيات غير معروفة: {string.Join(", ", unknown)}" });

        // The same self-protection rule as the role change: an admin may not strip its own management permission.
        if (request.Permissions is not null && user.Id == _tenant.UserId && !keys.Contains(Permissions.Admin))
            return BadRequest(new { message = "لا يمكنك إزالة صلاحية إدارة العيادة عن نفسك" });

        // null back to the role's defaults; anything else (even empty) is a deliberate explicit set.
        user.Permissions = request.Permissions == null ? null : string.Join(",", keys);
        await _db.SaveChangesAsync();
        return Ok(new
        {
            IsExplicit = user.Permissions != null,
            Effective = Permissions.Effective(user)
        });
    }

    /// <summary>
    /// PLAN.md 10.6: logical delete. The global query filter (ClinicDbContext) hides the account from every
    /// list and login refuses it (AuthController), while its visits and audit entries keep pointing at the
    /// untouched row. The username is renamed ("name-del-{id}") so the globally unique name is free for a new
    /// account to reuse. An admin may delete neither its own account nor the clinic's last active admin.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();

        if (user.Id == _tenant.UserId)
            return BadRequest(new { message = "لا يمكنك حذف حسابك بنفسك" });

        // The clinic must always keep a way in: an active admin cannot be deleted while it is the only one left.
        if (user.Role == UserRole.Admin && user.IsActive &&
            !await _db.Users.AnyAsync(u => u.Id != id && u.Role == UserRole.Admin && u.IsActive))
            return BadRequest(new { message = "لا يمكن حذف آخر حساب مدير نشط في العيادة" });

        user.IsDeleted = true;
        user.DeletedAtUtc = DateTime.UtcNow;
        user.IsActive = false;
        user.TokenVersion++; // PLAN_ADDENDUM.md 7.1: any token issued before the delete dies within the cache window
        user.Username = $"{user.Username}-del-{user.Id}"; // free the globally unique name for reuse
        _db.LogAction("Deleted", "User", user.Id);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, ResetPasswordRequest request)
    {
        var (valid, message) = PasswordPolicy.Validate(request.NewPassword);
        if (!valid)
            return BadRequest(new { message });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();

        user.PasswordHash = _hasher.HashPassword(user, request.NewPassword);
        user.TokenVersion++; // PLAN_ADDENDUM.md 7.1: a new password voids every token issued with the old one
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
