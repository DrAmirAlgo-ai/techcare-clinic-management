using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Queue;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Tests.Infrastructure;

/// <summary>
/// The rows clinic isolation tests need, one set per clinic (PLAN_ADDENDUM.md 0.2). Each test asks for the other
/// clinic's rows by their real ids and expects a 404 (or an empty list), never the row itself.
///
/// Both clinics deliberately share the same patient file number and the same phone number: those are the two
/// fields a leaking query would silently merge, so a broken filter shows up immediately instead of staying hidden.
/// </summary>
public sealed record ClinicFixture(
    int ClinicId,
    string Slug,
    string PatientName,
    string PatientFirstName,
    int PatientId,
    int VisitId,
    string Diagnosis,
    int BookedAppointmentId,
    string BookedTrackingToken,
    int WaitingAppointmentId,
    int InvoiceId,
    decimal InvoicePaid);

public static class ClinicFixtures
{
    /// <summary>Same file number in both clinics (the unique index is on <c>(ClinicId, Code)</c>).</summary>
    public const string SharedPatientCode = "10001";

    /// <summary>Same phone number in both clinics, so a search or a booking can never link the wrong file.</summary>
    public const string SharedPhone = "01000000099";

    /// <summary>Clinic A comes from the app's own first-run seeder.</summary>
    public static Task<ClinicFixture> ClinicAAsync(ApiFactory factory) =>
        factory.EnsureOnceAsync(nameof(ClinicAAsync), () => factory.WithServicesAsync(services =>
            CreateAsync(services, TestEnvironment.ClinicASlug, "أحمد عادل", "ارتفاع ضغط الدم",
                isUrgent: false, queueNumber: 2, invoicePaid: 500m)));

    /// <summary>Clinic B is created by <see cref="TestData.EnsureClinicBAsync"/> first.</summary>
    public static Task<ClinicFixture> ClinicBAsync(ApiFactory factory) =>
        factory.EnsureOnceAsync(nameof(ClinicBAsync), async () =>
        {
            await TestData.EnsureClinicBAsync(factory);
            return await factory.WithServicesAsync(services =>
                CreateAsync(services, TestEnvironment.ClinicBSlug, "بسام فؤاد", "التهاب شعبي حاد",
                    isUrgent: true, queueNumber: 1, invoicePaid: 777m));
        });

    /// <summary>
    /// Gives a slot inside <paramref name="clinicId"/> to that clinic, so a test can prove it stays bookable
    /// for the other clinic.
    /// </summary>
    public static Task<bool> BookSlotAsync(ApiFactory factory, int clinicId, int patientId, string patientName, DateTime at) =>
        factory.WithClinicAsync(clinicId, async db =>
        {
            db.Appointments.Add(new Appointment
            {
                PatientId = patientId,
                PatientName = patientName,
                ScheduledAt = at,
                Source = AppointmentSource.Reception,
                TrackingToken = QueueService.NewTrackingToken()
            });
            await db.SaveChangesAsync();
            return true;
        });

    /// <summary>
    /// Adds a patient row straight into one clinic. The public-booking test needs a patient file in the *other*
    /// clinic carrying the phone number that is about to be booked.
    /// </summary>
    public static Task<Patient> AddPatientAsync(ApiFactory factory, int clinicId, string fullName, string phone) =>
        factory.WithClinicAsync(clinicId, async db =>
        {
            var clinic = await db.Clinics.FirstAsync(c => c.Id == clinicId);
            var patient = new Patient { Code = "20001", FullName = fullName, Phone = phone, CreatedAt = ClinicClock.Now(clinic) };
            db.Patients.Add(patient);
            await db.SaveChangesAsync();
            return patient;
        });

    private static async Task<ClinicFixture> CreateAsync(
        IServiceProvider services, string slug, string patientName, string diagnosis,
        bool isUrgent, int queueNumber, decimal invoicePaid)
    {
        var db = services.GetRequiredService<ClinicDbContext>();
        var tenant = services.GetRequiredService<TenantContext>();

        var clinic = await db.Clinics.FirstAsync(c => c.Slug == slug);   // clinics themselves are not tenant scoped
        tenant.SetClinic(clinic.Id);                                     // every query and insert below is now scoped
        var now = ClinicClock.Now(clinic);

        var doctor = await db.Users.FirstAsync(u => u.Role == UserRole.Doctor);

        var patient = new Patient
        {
            Code = SharedPatientCode,
            FullName = patientName,
            Phone = SharedPhone,
            Gender = "ذكر",
            CreatedAt = now
        };
        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        var visit = new Visit
        {
            PatientId = patient.Id,
            DoctorId = doctor.Id,
            VisitDate = now,
            ChiefComplaint = $"شكوى {patientName}",
            Diagnosis = diagnosis
        };
        visit.Prescription.Add(new PrescriptionItem { DrugName = $"دواء {patientName}", Dose = "1 قرص" });
        db.Visits.Add(visit);
        await db.SaveChangesAsync();
        var booked = new Appointment
        {
            PatientId = patient.Id,
            PatientName = patientName,
            Phone = SharedPhone,
            ScheduledAt = now,
            Source = AppointmentSource.Reception,
            TrackingToken = QueueService.NewTrackingToken()
        };
        var waiting = new Appointment
        {
            PatientId = patient.Id,
            PatientName = patientName,
            Phone = SharedPhone,
            ScheduledAt = now,
            Status = AppointmentStatus.Waiting,
            QueueNumber = queueNumber,
            CheckedInAt = now,
            IsUrgent = isUrgent,
            Source = AppointmentSource.Reception,
            TrackingToken = QueueService.NewTrackingToken()
        };
        db.Appointments.AddRange(booked, waiting);
        await db.SaveChangesAsync();

        var invoice = new Invoice
        {
            PatientId = patient.Id,
            VisitId = visit.Id,
            Description = "كشف",
            Amount = invoicePaid,
            Discount = 0,
            Paid = invoicePaid,
            Method = PaymentMethod.Cash,
            CreatedAt = now,
            CreatedByUserId = doctor.Id
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        return new ClinicFixture(
            clinic.Id, slug, patientName, patientName.Split(' ')[0], patient.Id, visit.Id, diagnosis,
            booked.Id, booked.TrackingToken, waiting.Id, invoice.Id, invoicePaid);
    }
}
