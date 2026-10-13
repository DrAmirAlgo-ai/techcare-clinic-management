namespace TechCare.Clinic.Api.Tests.Infrastructure;

/// <summary>
/// Everything the test host needs to boot the real API on a machine that has no LM Studio, no PostgreSQL and
/// no seeded clinic (PLAN_ADDENDUM.md 0.1).
///
/// Configuration is pushed through <b>environment variables</b> on purpose: <c>Program.cs</c> reads
/// <c>WebApplication.CreateBuilder</c> configuration immediately (it throws when <c>Jwt:Key</c> is missing, and
/// <c>SeedData</c> needs the seed passwords), while the callbacks registered by
/// <c>WebApplicationFactory.ConfigureWebHost</c> only run when the host is built — too late. .NET maps
/// <c>Section__Key</c> to the configuration key <c>Section:Key</c>, and environment variables win over
/// appsettings.json, so this also switches the database and the library folder away from their real values.
/// </summary>
public static class TestEnvironment
{
    /// <summary>Environment name used by the test host; keeps <c>appsettings.Development.json</c> out of the run.</summary>
    public const string EnvironmentName = "Testing";

    /// <summary>
    /// One named in-memory SQLite database shared by the whole test process. The single
    /// <see cref="ApiFactory"/> instance keeps it alive with an open connection.
    /// </summary>
    public const string SqliteConnectionString = "Data Source=techcare_api_tests;Mode=Memory;Cache=Shared";

    /// <summary>Signing key for the test host; long enough for the API's 32 character minimum.</summary>
    public const string JwtKey = "integration-tests-only-key-not-for-production-0123456789";

    /// <summary>Throwaway folder that stands in for the real <c>MedicalLibrary</c>.</summary>
    public static string LibraryPath => Path.Combine(Path.GetTempPath(), "techcare-api-tests", "MedicalLibrary");

    /// <summary>Throwaway folder that stands in for the visit-attachment store (PLAN_FULL.md 5.1).</summary>
    public static string AttachmentsPath => Path.Combine(Path.GetTempPath(), "techcare-api-tests", "PatientFiles");

    /// <summary>
    /// Where the test host's rotating log file lands (PLAN_ADDENDUM.md 7.7): the correlation-id test reads it to
    /// prove the log carries the same request id the response returned.
    /// </summary>
    public static string LogFilePath => Path.Combine(Path.GetTempPath(), "techcare-api-tests", "logs", "app-.log");

    // ── Clinic A: created by the application's own first-run seeder ─────────────
    public const string ClinicASlug = "test-clinic-a";
    public const string ClinicAName = "عيادة الاختبار أ";
    public const string ClinicADoctorName = "د. الاختبار أ";
    public const string ClinicASpecialty = "باطنة عامة";
    public const string ClinicAAdminPassword = "TestAdmin@2026";
    public const string ClinicADoctorPassword = "TestDoctor@2026";
    public const string ClinicAReceptionPassword = "TestReception@2026";

    // ── Clinic B: created by TestData.EnsureClinicBAsync ────────────────────────
    public const string ClinicBSlug = "test-clinic-b";
    public const string ClinicBName = "عيادة الاختبار ب";
    public const string ClinicBAdminPassword = "TestAdminB@2026";
    public const string ClinicBDoctorPassword = "TestDoctorB@2026";
    public const string ClinicBReceptionPassword = "TestReceptionB@2026";

    // ── The installation owner (PLAN.md 1.1): created by the app's own seeder, attached to no clinic ───────
    /// <summary>
    /// The slug tests pass around for the owner account. It names no clinic — the owner belongs to none, which is
    /// exactly what <see cref="TestData.Credentials"/> needs to keep the owner out of the clinic accounts.
    /// </summary>
    public const string OwnerSlug = "owner";
    public const string OwnerUsername = "owner";
    public const string OwnerPassword = "OwnerTest@2026";
    public const string OwnerName = "مالك النظام (اختبار)";

    private static int _applied;

    /// <summary>
    /// Writes the configuration above into the process environment exactly once. Must run before the first
    /// <c>WebApplicationFactory.CreateClient()</c> call, i.e. from the <see cref="ApiFactory"/> constructor.
    /// </summary>
    internal static void ApplyToProcess()
    {
        if (Interlocked.Exchange(ref _applied, 1) == 1) return;

        Directory.CreateDirectory(LibraryPath);

        Set("ASPNETCORE_ENVIRONMENT", EnvironmentName);
        Set("DOTNET_ENVIRONMENT", EnvironmentName);

        Set("Database__Provider", "Sqlite");
        Set("ConnectionStrings__Sqlite", SqliteConnectionString);

        Set("Jwt__Key", JwtKey);
        Set("Jwt__Issuer", "TechCareClinic.Tests");
        Set("Jwt__Audience", "TechCareClinic.Tests");
        Set("Jwt__ExpiresMinutes", "60");

        // The login limiter is 10 per 5 minutes per IP in production; every test request here comes from one fake
        // IP, so the suite gets its own budget instead of tripping the limiter.
        Set("RateLimit__LoginPermitLimit", "1000");

        Set("Seed__ClinicName", ClinicAName);
        Set("Seed__ClinicSlug", ClinicASlug);
        Set("Seed__DoctorName", ClinicADoctorName);
        Set("Seed__Specialty", ClinicASpecialty);
        Set("Seed__AdminPassword", ClinicAAdminPassword);
        Set("Seed__DoctorPassword", ClinicADoctorPassword);
        Set("Seed__ReceptionPassword", ClinicAReceptionPassword);
        Set("Seed__OwnerUsername", OwnerUsername);
        Set("Seed__OwnerPassword", OwnerPassword);
        Set("Seed__OwnerName", OwnerName);

        // Nothing may leave the machine while tests run.
        Set("Ai__LibraryPath", LibraryPath);
        Set("Files__AttachmentsPath", AttachmentsPath);
        Set("Ai__External__Enabled", "false");
        Set("VectorStore__Enabled", "false");
        Set("VectorStore__ConnectionString", "");
        Set("Cors__AllowedOrigins__0", "http://localhost:5173");

        Set("Logging__LogLevel__Default", "Warning");
        Set("Logging__LogLevel__Microsoft.AspNetCore", "Warning");
        Set("Logging__FilePath", LogFilePath);
    }

    private static void Set(string name, string value) =>
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
}
