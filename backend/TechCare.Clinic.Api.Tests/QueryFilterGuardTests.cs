using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_ADDENDUM.md 0.2 acceptance criterion: these fail the moment somebody drops a <c>HasQueryFilter</c> from a
/// clinic-owned entity, before any behavioural test has to notice.
/// </summary>
[Collection(ApiCollection.Name)]
public class QueryFilterGuardTests
{
    private readonly ApiFactory _factory;

    public QueryFilterGuardTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void Every_clinic_owned_entity_is_filtered_on_the_current_clinic()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        // Accounts implement IClinicOptional instead of IClinicOwned — the SuperAdmin owner belongs to no clinic
        // (PLAN.md 1.1) — and the AI settings rows do the same (PLAN.md 1.2): a global row with no clinic plus
        // one row per clinic. They have to be filtered just the same, so both interfaces count here.
        var clinicOwned = db.Model.GetEntityTypes()
            .Where(e => typeof(IClinicOwned).IsAssignableFrom(e.ClrType)
                        || typeof(IClinicOptional).IsAssignableFrom(e.ClrType))
            .OrderBy(e => e.ClrType.Name)
            .ToList();

        Assert.Equal(
            new[] { nameof(AiSettings), nameof(Appointment), nameof(Invoice), nameof(Patient), nameof(QueueMessage), nameof(User), nameof(Visit), nameof(VisitAttachment) },
            clinicOwned.Select(e => e.ClrType.Name).ToArray());

        Assert.All(clinicOwned, entity => Assert.NotEmpty(entity.GetDeclaredQueryFilters()));
    }

    [Fact]
    public async Task Without_a_clinic_in_the_request_every_tenant_table_is_empty()
    {
        await ClinicFixtures.ClinicAAsync(_factory);   // the rows exist, so "empty" really means "filtered out"

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        Assert.Empty(await db.Patients.ToListAsync());
        Assert.Empty(await db.Visits.ToListAsync());
        Assert.Empty(await db.Appointments.ToListAsync());
        Assert.Empty(await db.Invoices.ToListAsync());

        // Accounts are the one table where "no clinic" means something: a row is visible only when it belongs to no
        // clinic at all, which is the SuperAdmin owner (PLAN.md 1.1). Every clinic's staff stays hidden.
        var users = await db.Users.ToListAsync();
        Assert.All(users, u => Assert.Null(u.ClinicId));
        Assert.DoesNotContain(users, u => u.Username == "admin");

        Assert.NotEmpty(await db.Clinics.ToListAsync());   // clinics are the tenant list itself, not tenant scoped
    }
}
