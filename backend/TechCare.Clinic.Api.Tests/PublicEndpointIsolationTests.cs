using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Tests.Infrastructure;
using static TechCare.Clinic.Api.Tests.Infrastructure.TestHttp;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_ADDENDUM.md 0.2, anonymous side — everything under <c>/api/public</c> is addressed by clinic slug (or by a
/// tracking token) and must never mix clinics: the doctor's site, the waiting-room screen and the booking form each
/// resolve exactly one clinic and then only touch that clinic's rows.
///
/// These are the only tests that call the "booking" rate-limited endpoints, which allow 5 calls per 10 minutes from
/// the test host's single IP, so the booking tests here stay few and deliberate.
/// </summary>
[Collection(ApiCollection.Name)]
public class PublicEndpointIsolationTests
{
    private readonly ApiFactory _factory;

    public PublicEndpointIsolationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Each_slug_serves_its_own_clinic()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        await ClinicFixtures.ClinicBAsync(_factory);
        using var client = _factory.CreateClient();

        var mine = await JsonAsync(await client.GetAsync($"/api/public/clinics/{TestEnvironment.ClinicASlug}"));
        var theirs = await JsonAsync(await client.GetAsync($"/api/public/clinics/{TestEnvironment.ClinicBSlug}"));
        var unknown = await client.GetAsync("/api/public/clinics/no-such-clinic");

        Assert.Equal(TestEnvironment.ClinicAName, mine.GetProperty("name").GetString());
        Assert.Equal(TestEnvironment.ClinicBName, theirs.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_waiting_room_screen_shows_the_patients_of_its_own_clinic_only()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var b = await ClinicFixtures.ClinicBAsync(_factory);
        using var client = _factory.CreateClient();

        var mine = await (await client.GetAsync($"/api/public/clinics/{TestEnvironment.ClinicASlug}/queue")).Content.ReadAsStringAsync();
        var theirs = await (await client.GetAsync($"/api/public/clinics/{TestEnvironment.ClinicBSlug}/queue")).Content.ReadAsStringAsync();

        Assert.Contains(a.PatientFirstName, mine);
        Assert.DoesNotContain(b.PatientFirstName, mine);
        Assert.Contains(b.PatientFirstName, theirs);
        Assert.DoesNotContain(a.PatientFirstName, theirs);
    }

    [Fact]
    public async Task A_booking_lands_in_the_clinic_of_the_slug_only()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var b = await ClinicFixtures.ClinicBAsync(_factory);
        using var client = _factory.CreateClient();
        var appointmentsInTheirClinic = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.CountAsync());

        var (date, time) = await FirstFreeSlotAsync(client, date => $"/api/public/clinics/{TestEnvironment.ClinicASlug}/slots?date={date:yyyy-MM-dd}");
        var response = await client.PostAsJsonAsync($"/api/public/clinics/{TestEnvironment.ClinicASlug}/bookings", new
        {
            name = "مريض الحجز الإلكتروني",
            phone = "01055556666",
            date = date.ToString("yyyy-MM-dd"),
            time,
            type = "Consultation"
        });
        response.EnsureSuccessStatusCode();
        var token = (await JsonAsync(response)).GetProperty("trackingToken").GetString()!;

        var booked = await _factory.WithClinicAsync(a.ClinicId, db => db.Appointments.FirstOrDefaultAsync(x => x.TrackingToken == token));
        Assert.NotNull(booked);
        Assert.Equal(a.ClinicId, booked!.ClinicId);
        Assert.Equal(appointmentsInTheirClinic, await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.CountAsync()));
    }

    [Fact]
    public async Task A_booking_never_links_a_patient_file_from_another_clinic()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var b = await ClinicFixtures.ClinicBAsync(_factory);
        using var client = _factory.CreateClient();

        // The same phone number, but this file lives in the other clinic: the booking must not adopt it.
        var theirPatient = await ClinicFixtures.AddPatientAsync(_factory, b.ClinicId, "مريض عيادة أخرى", "01077778888");

        var (date, time) = await FirstFreeSlotAsync(client, date => $"/api/public/clinics/{TestEnvironment.ClinicASlug}/slots?date={date:yyyy-MM-dd}");
        var response = await client.PostAsJsonAsync($"/api/public/clinics/{TestEnvironment.ClinicASlug}/bookings", new
        {
            name = "مريض الحجز الإلكتروني ب",
            phone = theirPatient.Phone,
            date = date.ToString("yyyy-MM-dd"),
            time,
            type = "Consultation"
        });
        response.EnsureSuccessStatusCode();
        var token = (await JsonAsync(response)).GetProperty("trackingToken").GetString()!;

        var booked = await _factory.WithClinicAsync(a.ClinicId, db => db.Appointments.FirstAsync(x => x.TrackingToken == token));
        Assert.Equal(a.ClinicId, booked.ClinicId);
        Assert.Null(booked.PatientId);
    }

    [Fact]
    public async Task Cancelling_with_a_tracking_token_touches_only_that_clinic()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var b = await ClinicFixtures.ClinicBAsync(_factory);
        using var client = _factory.CreateClient();

        var tracked = await JsonAsync(await client.GetAsync($"/api/public/track/{b.BookedTrackingToken}"));
        Assert.Equal(TestEnvironment.ClinicBName, tracked.GetProperty("clinic").GetProperty("name").GetString());

        var cancel = await client.PostAsync($"/api/public/track/{b.BookedTrackingToken}/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

        var theirBooking = await _factory.WithClinicAsync(b.ClinicId, db => db.Appointments.FirstAsync(x => x.Id == b.BookedAppointmentId));
        Assert.Equal(AppointmentStatus.Cancelled, theirBooking.Status);

        var myBooking = await _factory.WithClinicAsync(a.ClinicId, db => db.Appointments.FirstAsync(x => x.Id == a.BookedAppointmentId));
        Assert.Equal(AppointmentStatus.Booked, myBooking.Status);   // my own booking was not touched
    }
}
