namespace TechCare.Clinic.Api.Infrastructure;

/// <summary>
/// PLAN.md 10.2 — the installation's subscription settings. The default free-trial length applies to every
/// clinic the owner registers until that clinic gets its own <c>Clinic.TrialDays</c> override.
/// </summary>
public class SubscriptionOptions
{
    public int DefaultTrialDays { get; set; } = 14;
}
