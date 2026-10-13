using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;

namespace TechCare.Clinic.Api.Tests.Infrastructure;

/// <summary>
/// The data every test may rely on: clinic A comes from the app's own first-run seeder, clinic B is created
/// here so tests can prove that one clinic can never see or change another clinic's records.
/// </summary>
public static class TestData
{
    public const string AdminRole = nameof(UserRole.Admin);
    public const string DoctorRole = nameof(UserRole.Doctor);
    public const string ReceptionRole = nameof(UserRole.Reception);

    /// <summary>
    /// The installation owner (PLAN.md 1.1). Its account belongs to no clinic, so it is refused by every clinic
    /// endpoint and only <c>api/owner</c> accepts it.
    /// </summary>
    public const string SuperAdminRole = nameof(UserRole.SuperAdmin);

    /// <summary>The three clinic roles, in the order the controllers list them.</summary>
    public static readonly string[] AllRoles = { AdminRole, DoctorRole, ReceptionRole };

    /// <summary>Every account type the API knows, the owner included — the universe a role check is measured against.</summary>
    public static readonly string[] EveryRole = { AdminRole, DoctorRole, ReceptionRole, SuperAdminRole };

    private static readonly Dictionary<(string Clinic, string Role), (string Username, string Password)> Accounts = new()
    {
        [(TestEnvironment.ClinicASlug, AdminRole)] = ("admin", TestEnvironment.ClinicAAdminPassword),
        [(TestEnvironment.ClinicASlug, DoctorRole)] = ("doctor", TestEnvironment.ClinicADoctorPassword),
        [(TestEnvironment.ClinicASlug, ReceptionRole)] = ("reception", TestEnvironment.ClinicAReceptionPassword),

        // PLAN_ADDENDUM.md 7.2: usernames are now unique per clinic, so clinic B *may* reuse "admin" and friends.
        // It keeps the "-b" suffix anyway, because the tests read the credentials table and would otherwise have
        // to say which clinic they mean.
        [(TestEnvironment.ClinicBSlug, AdminRole)] = ("admin-b", TestEnvironment.ClinicBAdminPassword),
        [(TestEnvironment.ClinicBSlug, DoctorRole)] = ("doctor-b", TestEnvironment.ClinicBDoctorPassword),
        [(TestEnvironment.ClinicBSlug, ReceptionRole)] = ("reception-b", TestEnvironment.ClinicBReceptionPassword),

        // The owner account (PLAN.md 1.1) is attached to no clinic, so it is registered under the slug that stands
        // for "no clinic": TokenAsync(TestEnvironment.OwnerSlug, TestData.SuperAdminRole) signs in as the owner.
        [(TestEnvironment.OwnerSlug, SuperAdminRole)] = (TestEnvironment.OwnerUsername, TestEnvironment.OwnerPassword)
    };

    public static (string Username, string Password) Credentials(string clinicSlug, string role) =>
        Accounts.TryGetValue((clinicSlug, role), out var account)
            ? account
            : throw new ArgumentOutOfRangeException(nameof(clinicSlug), $"No test account for clinic '{clinicSlug}' and role '{role}'.");

    /// <summary>Creates clinic B with its three accounts and returns its id. Safe to call from any test.</summary>
    public static Task<int> EnsureClinicBAsync(ApiFactory factory) =>
        factory.EnsureOnceAsync(nameof(EnsureClinicBAsync), () => factory.WithServicesAsync(CreateClinicBAsync));

    private static async Task<int> CreateClinicBAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ClinicDbContext>();
        var existing = await db.Clinics.FirstOrDefaultAsync(c => c.Slug == TestEnvironment.ClinicBSlug);
        if (existing != null) return existing.Id;

        var clinic = new TechCare.Clinic.Api.Domain.Clinic
        {
            Name = TestEnvironment.ClinicBName,
            Slug = TestEnvironment.ClinicBSlug,
            DoctorName = "د. ب",
            Specialty = "أطفال",
            Phone = "01100000000",
            Address = "الجيزة",
            ConsultationFee = 200,
            FollowUpFee = 100,
            AvgMinutesPerPatient = 15,
            // PLAN.md 10.2: like the seeder's first clinic, the fixture keeps the full service (Pro, no end date),
            // so the role matrix's AI rows reach the assistant instead of the Basic-plan lock.
            Plan = ClinicPlan.Pro,
            SubscriptionStatus = SubscriptionStatus.Active
        };
        db.Clinics.Add(clinic);
        await db.SaveChangesAsync();

        var hasher = services.GetRequiredService<IPasswordHasher<User>>();
        foreach (var role in AllRoles)
        {
            var (username, password) = Credentials(TestEnvironment.ClinicBSlug, role);
            var user = new User
            {
                ClinicId = clinic.Id,
                Username = username,
                FullName = $"{username} ({TestEnvironment.ClinicBName})",
                Role = Enum.Parse<UserRole>(role)
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);
        }

        await db.SaveChangesAsync();
        return clinic.Id;
    }
}
