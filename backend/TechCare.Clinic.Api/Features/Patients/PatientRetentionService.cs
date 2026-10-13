using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TechCare.Clinic.Api.Data;

namespace TechCare.Clinic.Api.Features.Patients;

/// <summary>
/// PLAN_ADDENDUM.md 9.3: the physical half of patient data rights. A logically deleted patient file is kept
/// for the clinic's own retention period (<c>Clinic.DataRetentionDays</c>); once it has elapsed, this service
/// removes the patient row — and, through the cascades, its visits, prescriptions, invoices and attachments —
/// while a booking about that patient survives (its foreign key is SetNull, so the row simply loses the
/// patient reference). Every purged file joins the audit trail: the ordinary "Deleted" entity row plus an
/// explicit "Purged" event stamped with the system's name, because no user account performs this step.
/// </summary>
public class PatientRetentionService : BackgroundService
{
    /// <summary>How often the service re-checks for files whose retention period has elapsed.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PatientRetentionService> _logger;

    public PatientRetentionService(IServiceScopeFactory scopes, ILogger<PatientRetentionService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The app may have been down for days; settle whatever is already due before the timer takes over.
        await RunPurgeSafelyAsync(stoppingToken);

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunPurgeSafelyAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // The host shutting down is not a failure of the purge.
        }
    }

    /// <summary>
    /// Physically removes every logically deleted patient file whose clinic-level retention period has
    /// elapsed, and answers with how many files went. Kept public so the integration tests drive it
    /// deterministically instead of waiting for the timer.
    /// </summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        var deleted = await db.Patients.IgnoreQueryFilters()
            .Where(p => p.IsDeleted)
            .Select(p => new { p.Id, p.ClinicId, p.DeletedAtUtc })
            .ToListAsync(cancellationToken);

        var retention = await db.Clinics
            .ToDictionaryAsync(c => c.Id, c => c.DataRetentionDays, cancellationToken);

        var now = DateTime.UtcNow;
        var expiredIds = deleted
            .Where(p => p.DeletedAtUtc.HasValue
                        && retention.TryGetValue(p.ClinicId, out var days)
                        && p.DeletedAtUtc.Value.AddDays(days) <= now)
            .Select(p => p.Id)
            .ToList();
        if (expiredIds.Count == 0) return 0;

        var patients = await db.Patients.IgnoreQueryFilters()
            .Where(p => expiredIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        foreach (var patient in patients)
            db.LogAction("Purged", "Patient", patient.Id, userId: null, userName: "النظام", clinicId: patient.ClinicId);

        db.Patients.RemoveRange(patients);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Patient retention: purged {Count} expired patient file(s)", patients.Count);
        return patients.Count;
    }

    private async Task RunPurgeSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PurgeExpiredAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A purge failure must never take the clinic down; the next tick retries.
            _logger.LogError(ex, "Patient retention purge failed");
        }
    }
}
