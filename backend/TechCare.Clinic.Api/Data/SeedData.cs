using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Data;

public static class SeedData
{
    /// <summary>
    /// Creates the SuperAdmin owner account and, on an empty database, the first clinic with its staff accounts
    /// (PLAN.md 1.1). Passwords come from configuration (Seed:*Password) so production installs never ship with
    /// known defaults.
    /// </summary>
    public static async Task EnsureSeededAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        var db = services.GetRequiredService<ClinicDbContext>();
        var tenant = services.GetRequiredService<TenantContext>();
        var hasher = services.GetRequiredService<IPasswordHasher<User>>();

        // Checked before the clinic early-return, so an installation that is already running still gets its owner
        // account on the next start.
        await EnsureOwnerAsync(db, config, hasher, logger);

        if (await db.Clinics.AnyAsync())
            return;

        var clinic = new Domain.Clinic
        {
            Name = config["Seed:ClinicName"] ?? "عيادة د. أمير",
            Slug = config["Seed:ClinicSlug"] ?? "dr-amir",
            DoctorName = config["Seed:DoctorName"] ?? "د. أمير",
            Specialty = config["Seed:Specialty"] ?? "باطنة عامة",
            About = "استشاري الباطنة العامة. متابعة الأمراض المزمنة (السكر والضغط) والكشف الدوري.",
            Phone = "01000000000",
            Address = "القاهرة",
            ConsultationFee = 300,
            FollowUpFee = 150,
            AvgMinutesPerPatient = 15,
            // PLAN.md 10.2: the installation's first clinic existed before subscriptions did, so it keeps the full
            // service (Pro, no end date). Clinics registered later start on Basic in trial instead.
            Plan = Domain.ClinicPlan.Pro,
            SubscriptionStatus = Domain.SubscriptionStatus.Active
        };
        db.Clinics.Add(clinic);
        await db.SaveChangesAsync();
        tenant.SetClinic(clinic.Id);

        var accounts = new (string Username, string FullName, UserRole Role, string ConfigKey)[]
        {
            ("admin", "مدير العيادة", UserRole.Admin, "Seed:AdminPassword"),
            ("doctor", clinic.DoctorName, UserRole.Doctor, "Seed:DoctorPassword"),
            ("reception", "الاستقبال", UserRole.Reception, "Seed:ReceptionPassword")
        };

        foreach (var (username, fullName, role, key) in accounts)
        {
            var password = config[key];
            if (string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException($"Missing '{key}' in configuration; required to create the initial '{username}' account.");

            var user = new User { ClinicId = clinic.Id, Username = username, FullName = fullName, Role = role };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Seeded clinic '{Clinic}' (slug: {Slug}) with admin/doctor/reception accounts.", clinic.Name, clinic.Slug);
    }

    /// <summary>
    /// Creates the installation owner (PLAN.md 1.1) from <c>Seed:OwnerUsername</c> / <c>Seed:OwnerPassword</c>.
    /// The account is attached to no clinic, which is what separates it from a clinic Admin: it can only ever
    /// reach <c>api/owner</c>. Without a configured password nothing is created and a warning is logged instead —
    /// an installation that has no owner yet simply has no way into the owner panel.
    /// </summary>
    private static async Task EnsureOwnerAsync(ClinicDbContext db, IConfiguration config, IPasswordHasher<User> hasher, ILogger logger)
    {
        // Justified IgnoreQueryFilters: seeding runs before any request exists, so there is no clinic in the tenant
        // context and the account filter would hide every row — including the owner account we are looking for.
        var existing = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin);
        if (existing != null)
        {
            logger.LogInformation("Owner account '{Username}' is in place; nothing to seed.", existing.Username);
            return;
        }

        var password = config["Seed:OwnerPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("No SuperAdmin owner account yet. Set Seed:OwnerUsername and Seed:OwnerPassword (environment variable Seed__OwnerPassword) to create one.");
            return;
        }

        var username = (config["Seed:OwnerUsername"] ?? "owner").Trim().ToLowerInvariant();
        var owner = new User
        {
            ClinicId = null,
            Username = username,
            FullName = config["Seed:OwnerName"] ?? "مالك النظام",
            Role = UserRole.SuperAdmin
        };
        owner.PasswordHash = hasher.HashPassword(owner, password);
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded the SuperAdmin owner account '{Username}' (not attached to a clinic; sign in from /owner).", username);
    }
}
