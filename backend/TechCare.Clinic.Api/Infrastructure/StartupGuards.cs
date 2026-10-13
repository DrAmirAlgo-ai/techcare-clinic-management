namespace TechCare.Clinic.Api.Infrastructure;

/// <summary>
/// PLAN_ADDENDUM.md 7.6: a production deployment whose UI lives on a different origin while
/// <c>Cors:AllowedOrigins</c> is empty would boot, then fail on every cross-origin call. Failing at startup with
/// a message that points at the fix is cheaper. Kept as a plain static method so the check itself is unit-testable
/// without booting a second host.
/// </summary>
public static class StartupGuards
{
    public static void EnsureCorsConfiguredForProduction(string environmentName, IEnumerable<string> allowedOrigins)
    {
        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase)
            && !allowedOrigins.Any(o => !string.IsNullOrWhiteSpace(o)))
        {
            throw new InvalidOperationException(
                "Production requires Cors:AllowedOrigins to list the UI's origin (PLAN_ADDENDUM.md 7.6).");
        }
    }
}