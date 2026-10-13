using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Queue;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Tests.Infrastructure;

/// <summary>
/// Builds what a <see cref="RoleMatrixRow"/> request points at: the ids in its path, the rows its body refers
/// to, and the state those rows must be in for an allowed role to really get through (PLAN_ADDENDUM.md 0.3).
///
/// Two modes, because the three tests ask different questions:
/// <list type="bullet">
/// <item><see cref="AuthorizationOnlyAsync"/> for the 401/403 cases — the framework answers before the action
/// body runs, so nothing may be created and every id is a placeholder;</item>
/// <item><see cref="PreparedAsync"/> for the cases that must succeed — every id and every body belongs to a row
/// created here, so a passing call can never change a fixture row another test depends on. Both clinics are made
/// sure of first, so the matrix always runs against the two-clinic database the rest of the suite uses.</item>
/// </list>
/// </summary>
public sealed class RoleMatrixContext
{
    /// <summary>File numbers used here, far above the fixtures' 10001/20001 so the two can never collide.
    /// They are handed out as "RM" + this number (see <c>AddPatientAsync</c>) so the clinic's own file-number
    /// generator, which adds one to the largest number it can parse, never lands on one of them.</summary>
    private static int _sequence = 90000;

    private const string UploadName = "role-matrix-upload.txt";
    private const string DeleteName = "role-matrix-delete.txt";
    private const string DocumentName = "role-matrix-document.txt";
    private const string PngFileName = "role-matrix-attachment.png";

    /// <summary>Id used in authorization-only mode: it only has to match a route, never a real row.</summary>
    private const int PlaceholderId = 1;

    private readonly ApiFactory _factory;
    private readonly bool _real;

    private RoleMatrixContext(ApiFactory factory, int clinicId, bool real)
    {
        _factory = factory;
        ClinicId = clinicId;
        _real = real;
    }

    /// <summary>Clinic A's id: every request in the matrix is sent from that clinic.</summary>
    public int ClinicId { get; }

    /// <summary>Mode for the anonymous and forbidden cases; no row is created and no file is written.</summary>
    public static async Task<RoleMatrixContext> AuthorizationOnlyAsync(ApiFactory factory)
    {
        var clinic = await ClinicFixtures.ClinicAAsync(factory);
        await TestData.EnsureClinicBAsync(factory);
        return new RoleMatrixContext(factory, clinic.ClinicId, real: false);
    }

    /// <summary>Mode for the cases that must reach their action and succeed.</summary>
    public static async Task<RoleMatrixContext> PreparedAsync(ApiFactory factory)
    {
        var clinic = await ClinicFixtures.ClinicAAsync(factory);
        await TestData.EnsureClinicBAsync(factory);
        return new RoleMatrixContext(factory, clinic.ClinicId, real: true);
    }

    /// <summary>The fixture patient of clinic A, for the read-only endpoints that take a patient id.</summary>
    public async Task<int> FixturePatientIdAsync() =>
        _real ? (await ClinicFixtures.ClinicAAsync(_factory)).PatientId : PlaceholderId;

    /// <summary>The fixture visit of clinic A, for the read-only visit report.</summary>
    public async Task<int> FixtureVisitIdAsync() =>
        _real ? (await ClinicFixtures.ClinicAAsync(_factory)).VisitId : PlaceholderId;

    /// <summary>
    /// An empty patient file of its own, so a write can never touch a fixture file. It is created once per factory
    /// and shared by every row: <c>GET api/patients</c> answers with the newest 50 files, so a file per case would
    /// push the patients other tests assert on off that first page.
    /// </summary>
    public async Task<int> NewPatientIdAsync()
    {
        if (!_real) return PlaceholderId;
        return (await MatrixPatientAsync()).Id;
    }

    /// <summary>
    /// A file that really is thrown away, for the single row that deletes a patient: deleting the shared file
    /// would take it away from every row that follows.
    /// </summary>
    public async Task<int> NewThrowawayPatientIdAsync()
    {
        if (!_real) return PlaceholderId;
        return await InClinicAsync(async (_, db) => (await AddPatientAsync(db, NextCode())).Id);
    }

    /// <summary>The one patient every row of the matrix points at, created once per factory.</summary>
    private Task<Patient> MatrixPatientAsync() =>
        _factory.EnsureOnceAsync("role-matrix:patient", () => InClinicAsync((_, db) => AddPatientAsync(db, NextCode())));

    /// <summary>A visit recorded directly, for the endpoints that edit or print a visit.</summary>
    public async Task<int> NewVisitIdAsync()
    {
        if (!_real) return PlaceholderId;
        var patientId = await NewPatientIdAsync();
        return await InClinicAsync(async (_, db) =>
        {
            var clinic = await ClinicAsync(db);
            var visit = new Visit
            {
                PatientId = patientId,
                DoctorId = await DoctorIdAsync(db),
                VisitDate = ClinicClock.Now(clinic),
                ChiefComplaint = "شكوى مصفوفة الأدوار",
                Diagnosis = "تشخيص مصفوفة الأدوار"
            };
            db.Visits.Add(visit);
            await db.SaveChangesAsync();
            return visit.Id;
        });
    }

    /// <summary>A PNG byte stream the signature check accepts: its 8-byte magic and first chunk header, a body the
    /// endpoint never parses — the browser preview is the point, and the test only needs a real signature.</summary>
    private static byte[] PngBytes() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x49, 0x44, 0x41,
        0x54, 0x28, 0xCE, 0x49, 0x00, 0x01, 0x00, 0x02
    ];

    /// <summary>The image the attachment row uploads; an allowed call proves the content passes the signature check.</summary>
    public static HttpContent PngUploadContent()
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(PngBytes());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", PngFileName);
        return content;
    }

    /// <summary>
    /// An attachment the open row can read back (PLAN_FULL.md 5.1): a row in the database and the file's own bytes on
    /// disk in the clinic's attachment folder, so the allowed case always serves a file that really exists.
    /// </summary>
    public async Task<int> NewAttachmentIdAsync()
    {
        if (!_real) return PlaceholderId;
        var visitId = await NewVisitIdAsync();
        return await InClinicAsync(async (_, db) =>
        {
            var attachment = new VisitAttachment
            {
                VisitId = visitId,
                StoredName = PngFileName,
                OriginalName = "مرفق مصفوفة الأدوار",
                ContentType = "image/png",
                SizeBytes = PngBytes().Length,
                UploadedAtUtc = DateTime.UtcNow
            };
            db.VisitAttachments.Add(attachment);
            await db.SaveChangesAsync();

            var folder = Path.Combine(TestEnvironment.AttachmentsPath, ClinicId.ToString());
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, PngFileName), PngBytes());
            return attachment.Id;
        });
    }

    /// <summary>A booked appointment inside today: the state check-in and no-show expect.</summary>
    public Task<(int Id, DateTime ScheduledAt)> NewBookedAppointmentAsync() =>
        NewAppointmentAsync(AppointmentStatus.Booked, isUrgent: false);

    /// <summary>A patient waiting in today's queue: the state start, finish, postpone and urgent expect.</summary>
    public Task<(int Id, DateTime ScheduledAt)> NewWaitingAppointmentAsync(bool isUrgent) =>
        NewAppointmentAsync(AppointmentStatus.Waiting, isUrgent);

    /// <summary>An unpaid invoice of its own, so a payment can never change a fixture invoice.</summary>
    public async Task<int> NewInvoiceIdAsync()
    {
        if (!_real) return PlaceholderId;
        var patientId = await NewPatientIdAsync();
        return await InClinicAsync(async (_, db) =>
        {
            var clinic = await ClinicAsync(db);
            var invoice = new Invoice
            {
                PatientId = patientId,
                Description = "كشف مصفوفة الأدوار",
                Amount = 100m,
                Discount = 0m,
                Paid = 0m,
                Method = PaymentMethod.Cash,
                CreatedAt = ClinicClock.Now(clinic),
                CreatedByUserId = await DoctorIdAsync(db)
            };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            return invoice.Id;
        });
    }

    /// <summary>An account of its own, so editing or resetting a user never touches a seeded account.</summary>
    public async Task<int> NewUserIdAsync()
    {
        if (!_real) return PlaceholderId;
        var username = $"matrix{NextCode()}";
        return await InClinicAsync(async (services, db) =>
        {
            var user = new User
            {
                ClinicId = ClinicId,
                FullName = "مستخدم مصفوفة الأدوار",
                Username = username,
                Role = UserRole.Reception
            };
            user.PasswordHash = services.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, "Matrix@2026");
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user.Id;
        });
    }

    /// <summary>A unique username for the create-account row.</summary>
    public string NewUsername() => $"matrixcreate{NextCode()}";

    /// <summary>
    /// A throwaway account of the given role, signed in through the real login endpoint. The change-password row
    /// runs as this account so its allowed case can never rewrite a fixture account's password (PLAN_ADDENDUM.md 7.5).
    /// </summary>
    public async Task<string> MatrixUserTokenAsync(string role)
    {
        if (!_real)
            throw new InvalidOperationException("change-password needs a real account, so it only runs in prepared mode.");

        var username = $"matrixpw{NextCode()}";
        const string password = "MatrixPw@2026";
        // The endpoint carries no role restriction, so the account's role is immaterial to the call — except that
        // a second SuperAdmin row would break the owner panel's "exactly one owner" test, so the owner case signs
        // in through a plain clinic account instead.
        var accountRole = role == TestData.SuperAdminRole ? UserRole.Reception : Enum.Parse<UserRole>(role);
        await InClinicAsync(async (services, db) =>
        {
            var user = new User
            {
                ClinicId = ClinicId,
                FullName = "تغيير كلمة المرور",
                Username = username,
                Role = accountRole
            };
            user.PasswordHash = services.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return 0;
        });

        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>The first slot the appointments endpoints call bookable, read from the API itself.</summary>
    public async Task<(DateOnly Date, string Time)> FreeSlotAsync()
    {
        if (!_real) return (DateOnly.FromDateTime(DateTime.Today).AddDays(1), "09:00");
        using var client = await AdminClientAsync();
        return await TestHttp.FirstFreeSlotAsync(client, date => $"/api/appointments/slots?date={date:yyyy-MM-dd}");
    }

    /// <summary>
    /// Clinic A's current settings, echoed back by the update row: a role test must not change the settings
    /// another test asserts on.
    /// </summary>
    public async Task<object> CurrentSettingsAsync()
    {
        if (!_real)
            return new
            {
                name = TestEnvironment.ClinicAName,
                slug = TestEnvironment.ClinicASlug,
                doctorName = TestEnvironment.ClinicADoctorName,
                specialty = TestEnvironment.ClinicASpecialty,
                about = (string?)null,
                phone = (string?)null,
                address = (string?)null,
                consultationFee = 100m,
                followUpFee = 50m,
                avgMinutesPerPatient = 15,
                workStart = "09:00",
                workEnd = "17:00",
                workDays = "Sat,Sun,Mon,Tue,Wed,Thu",
                onlineBookingEnabled = true,
                dataRetentionDays = 30
            };

        using var client = await AdminClientAsync();
        using var response = await client.GetAsync("/api/clinic");
        response.EnsureSuccessStatusCode();
        var body = await TestHttp.JsonAsync(response);
        return new
        {
            name = Text(body, "name"),
            slug = Text(body, "slug"),
            doctorName = Text(body, "doctorName"),
            specialty = Text(body, "specialty"),
            about = Text(body, "about"),
            phone = Text(body, "phone"),
            address = Text(body, "address"),
            consultationFee = body.GetProperty("consultationFee").GetDecimal(),
            followUpFee = body.GetProperty("followUpFee").GetDecimal(),
            avgMinutesPerPatient = body.GetProperty("avgMinutesPerPatient").GetInt32(),
            workStart = Text(body, "workStart"),
            workEnd = Text(body, "workEnd"),
            workDays = Text(body, "workDays"),
            onlineBookingEnabled = body.GetProperty("onlineBookingEnabled").GetBoolean(),
            dataRetentionDays = body.GetProperty("dataRetentionDays").GetInt32()
        };
    }

    /// <summary>A string property of a settings response; the API omits null ones, so a missing one is null.</summary>
    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    /// <summary>Writes a file the delete-a-document row is allowed to remove, and returns its relative name.</summary>
    public async Task<string> CreateLibraryFileAsync()
    {
        if (!_real) return DeleteName;
        await File.WriteAllTextAsync(Path.Combine(ApiFactory.LibraryPath, DeleteName), "ملف اختبار مصفوفة الأدوار.");
        return DeleteName;
    }

    /// <summary>Small in-memory upload, so an anonymous or forbidden caller writes nothing to disk.</summary>
    public static HttpContent UploadContent()
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("محتوى اختباري كامل لمصفوفة الأدوار، صالح للفهرسة في بيئة الاختبار."));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", UploadName);
        return content;
    }

    /// <summary>
    /// A real document the cited-file row can open (PLAN.md 1.3): written to the library folder, indexed through
    /// the owner's own reindex endpoint, and returned by its row id, so the allowed case always reads a file back.
    /// </summary>
    public async Task<int> NewLibraryDocumentIdAsync()
    {
        if (!_real) return PlaceholderId;
        // TextChunker yields no chunk for text of 40 characters or fewer, so the document's text stays comfortably above it.
        await File.WriteAllTextAsync(Path.Combine(ApiFactory.LibraryPath, DocumentName), "مستند مصفوفة الأدوار — نص كامل صالح للفهرسة وفتحه من الإجابة.");
        using var owner = await OwnerClientAsync();
        // A sweep that started before the file was written cannot pick it up, so: start (or join) one, wait it out,
        // and if the index still has no row for the file, run another sweep.
        for (var attempt = 0; ; attempt++)
        {
            using var started = await owner.PostAsync("/api/library/reindex", new StringContent(""));
            if (started.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.Conflict))
                throw new InvalidOperationException($"reindex answered {(int)started.StatusCode} for the owner.");
            await WaitForLibraryIdleAsync();

            var id = await InClinicAsync((_, db) =>
                db.LibraryDocuments.AsNoTracking().Where(d => d.FileName == DocumentName)
                    .Select(d => (int?)d.Id).FirstOrDefaultAsync());
            if (id is { } found) return found;
            if (attempt >= 2)
            {
                // Surface the index's own error list so a dead sweep is never silent.
                using var state = await owner.GetAsync("/api/library");
                throw new InvalidOperationException(
                    $"the library index did not pick up {DocumentName}. " +
                    (state.StatusCode == HttpStatusCode.OK
                        ? $"library state: {await state.Content.ReadAsStringAsync()}"
                        : $"library state unavailable: {(int)state.StatusCode}"));
            }
        }
    }

    /// <summary>
    /// Waits for a reindex started by an earlier call to finish, so the library rows answer from an idle library
    /// instead of racing a background run (a busy library answers 409, see <see cref="RoleMatrix"/>). The library
    /// is owner-only now (PLAN_ADDENDUM.md 7.3), so the matrix polls it with the owner's token.
    /// </summary>
    public async Task WaitForLibraryIdleAsync()
    {
        if (!_real) return;
        using var client = await OwnerClientAsync();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var response = await client.GetAsync("/api/library");
            if (response.IsSuccessStatusCode)
            {
                var body = await TestHttp.JsonAsync(response);
                if (body.TryGetProperty("status", out var status)
                    && status.TryGetProperty("isRunning", out var running)
                    && !running.GetBoolean())
                    return;
            }

            await Task.Delay(100);
        }
    }

    /// <summary>An admin client used only to arrange a row: reading a free slot or echoing the clinic's settings.</summary>
    private async Task<HttpClient> AdminClientAsync() =>
        _factory.CreateAuthenticatedClient(await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole));

    /// <summary>
    /// PLAN_ADDENDUM.md 7.3: the library's endpoints answer to the owner only, so the matrix arranges its library
    /// state (waiting out a reindex, indexing a cited document) with the owner's token instead of an admin's.
    /// </summary>
    private async Task<HttpClient> OwnerClientAsync() =>
        _factory.CreateAuthenticatedClient(await _factory.TokenAsync(TestEnvironment.OwnerSlug, TestData.SuperAdminRole));

    private async Task<(int Id, DateTime ScheduledAt)> NewAppointmentAsync(AppointmentStatus status, bool isUrgent)
    {
        if (!_real) return (PlaceholderId, At());

        var patientId = await NewPatientIdAsync();
        return await InClinicAsync(async (_, db) =>
        {
            var clinic = await ClinicAsync(db);
            var patient = await db.Patients.FirstAsync(p => p.Id == patientId);
            var appointment = new Appointment
            {
                PatientId = patient.Id,
                PatientName = patient.FullName,
                Phone = patient.Phone,
                ScheduledAt = ClinicClock.Now(clinic),
                Status = status,
                Type = AppointmentType.Consultation,
                Source = AppointmentSource.Reception,
                IsUrgent = isUrgent,
                Notes = "مصفوفة الأدوار",
                QueueNumber = status == AppointmentStatus.Waiting ? await NextQueueNumberAsync(clinic, db) : null,
                TrackingToken = QueueService.NewTrackingToken()
            };
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
            return (appointment.Id, appointment.ScheduledAt);
        });
    }

    private static async Task<int> NextQueueNumberAsync(TechCare.Clinic.Api.Domain.Clinic clinic, ClinicDbContext db)
    {
        var start = ClinicClock.Now(clinic).Date;
        var today = await db.Appointments.IgnoreQueryFilters()
            .Where(a => a.ClinicId == clinic.Id && a.ScheduledAt >= start && a.ScheduledAt < start.AddDays(1))
            .Select(a => a.QueueNumber)
            .ToListAsync();
        return today.Where(n => n.HasValue).Select(n => n!.Value).DefaultIfEmpty(0).Max() + 1;
    }

    /// <summary>
    /// A patient of its own for a write to point at. <see cref="NextCode"/> hands out ints while
    /// <see cref="Patient.Code"/> is the text the clinic formats, so the conversion happens here, once.
    ///
    /// The code carries an "RM" prefix on purpose. <c>PatientCodeGenerator</c> hands the clinic's next walk-in
    /// "the largest file number it can parse, plus one" — a numeric-only code from this file would make the very
    /// next matrix code the clinic's next walk-in number too, and the insert would fail on
    /// <c>Patients (ClinicId, Code)</c>. Letters are skipped by that generator, so the two series stay apart.
    /// </summary>
    private async Task<Patient> AddPatientAsync(ClinicDbContext db, int sequence)
    {
        var code = $"RM{sequence}";
        var clinic = await ClinicAsync(db);
        var patient = new Patient
        {
            Code = code,
            FullName = $"مراجع مصفوفة الأدوار {code}",
            Phone = $"0111{sequence}",
            CreatedAt = ClinicClock.Now(clinic)
        };
        db.Patients.Add(patient);
        await db.SaveChangesAsync();
        return patient;
    }

    private static Task<int> DoctorIdAsync(ClinicDbContext db) =>
        db.Users.Where(u => u.Role == UserRole.Doctor).Select(u => u.Id).FirstAsync();

    private static Task<TechCare.Clinic.Api.Domain.Clinic> ClinicAsync(ClinicDbContext db) =>
        db.Clinics.FirstAsync(c => c.Id == db.CurrentClinicId);

    private Task<T> InClinicAsync<T>(Func<IServiceProvider, ClinicDbContext, Task<T>> action) =>
        _factory.WithServicesAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().SetClinic(ClinicId);
            return await action(services, services.GetRequiredService<ClinicDbContext>());
        });

    /// <summary>Inside the clinic, truncated to the second so the value survives a JSON round trip unchanged.</summary>
    private static DateTime At()
    {
        var now = DateTime.Now;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
    }

    private static int NextCode() => Interlocked.Increment(ref _sequence);
}
