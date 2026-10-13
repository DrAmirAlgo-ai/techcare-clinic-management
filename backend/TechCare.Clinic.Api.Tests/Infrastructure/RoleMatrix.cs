using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TechCare.Clinic.Api.Infrastructure;
using System.Text;
using TechCare.Clinic.Api.Domain;



namespace TechCare.Clinic.Api.Tests.Infrastructure;

/// <summary>
/// One row of the endpoint × role matrix (PLAN_ADDENDUM.md 0.3): which roles may call an endpoint and the
/// request a test has to send to reach it. <see cref="Route"/> is the action's own route template
/// ("GET api/patients/{id:int}"), which is what lets <c>RoleMatrixTests</c> prove that no protected action is
/// missing from the table and that no row has outlived its endpoint.
///
/// Enums travel as their names ("Consultation", "Cash", "Reception") exactly like the frontend sends them, so a
/// row never depends on how the host configures JSON.
/// </summary>
public sealed record RoleMatrixRow(
    string Route,
    string[] AllowedRoles,
    Func<RoleMatrixContext, Task<HttpRequestMessage>> Request,
    HttpStatusCode[]? AllowedStatuses = null,
    Func<RoleMatrixContext, string, Task<string>>? Actor = null)
{
    /// <summary>
    /// Whether <paramref name="status"/> counts as "the allowed role got through": any 2xx, unless the row names
    /// its statuses itself (the two library rows also accept 409 — the endpoint's own answer while a background
    /// reindex is running).
    /// </summary>
    public bool Accepts(HttpStatusCode status) =>
        AllowedStatuses?.Contains(status) ?? (int)status is >= 200 and < 300;

    public override string ToString() => Route;
}

/// <summary>
/// Every protected endpoint of the API, the roles that may call it, and how a test reaches it with data it owns.
/// The table is the single source of truth for PLAN_ADDENDUM.md 0.3. Since PLAN.md 1.4 the clinic endpoints are
/// gated by per-account permission sets; the table's role lists then read as "the roles that carry this
/// permission by default" — the fixture accounts carry no explicit set, so they behave exactly as their roles
/// always did, and the introspection test maps a policy back to those roles through Permissions.DefaultRoles.
/// </summary>
public static class RoleMatrix
{
    /// <summary>The role sets, split out of the production constant so a change to <see cref="Roles"/> shows up here.</summary>
    private static readonly string[] Staff = Roles.Staff.Split(',');
    private static readonly string[] Clinical = Roles.Clinical.Split(',');
    private static readonly string[] AdminOnly = Roles.Admin.Split(',');
    private static readonly string[] SuperAdminOnly = Roles.SuperAdmin.Split(',');

    /// <summary>
    /// [Authorize] without roles (GET api/auth/me): any signed-in account, whatever its role — the owner included
    /// (PLAN.md 1.1), whose token carries no clinic.
    /// </summary>
    private static readonly string[] AnySignedIn = TestData.EveryRole;

    /// <summary>202 started, 409 a reindex was already running: both are the endpoint's own answers.</summary>
    private static readonly HttpStatusCode[] ReindexAnswers = { HttpStatusCode.Accepted, HttpStatusCode.Conflict };

    /// <summary>204 deleted, 409 the library was busy indexing at that moment.</summary>
    private static readonly HttpStatusCode[] DeleteAnswers = { HttpStatusCode.NoContent, HttpStatusCode.Conflict };
    private static byte[] PdfContent => Encoding.UTF8.GetBytes(@"%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>
endobj
4 0 obj
<< /Length 5 0 R >>
stream
BT
70 720 Td
(Hello) Tj
ET
endstream
endobj
5 0 obj
<< /Length 44 >>
endobj
xref
0 6
0000000000 65535 f 
0000000010 00000 n 
0000000053 00000 n 
0000000102 00000 n 
0000000259 00000 n 
0000000357 00000 n 
trailer
<< /Size 6 /Root 1 0 R >>
startxref
415
%%EOF");


    /// <summary>
    /// One case per (row, allowed role), as (row index, role). A theory can only carry values xunit knows how to
    /// name and order, so a case holds the index of its row rather than the row itself; <see cref="Row"/> turns it
    /// back, and the index in the test name is what points at the offending line of this table.
    /// </summary>
    public static IEnumerable<object[]> AllowedCases =>
        Rows.SelectMany((row, index) => row.AllowedRoles.Select(role => new object[] { index, role }));

    /// <summary>One case per (row, role that must be refused).</summary>
    public static IEnumerable<object[]> DeniedCases =>
        Rows.SelectMany((row, index) => TestData.EveryRole
            .Where(role => !row.AllowedRoles.Contains(role))
            .Select(role => new object[] { index, role }));

    /// <summary>One case per row, for the caller with no token at all.</summary>
    public static IEnumerable<object[]> EveryRow => Rows.Select((_, index) => new object[] { index });

    /// <summary>The row a case refers to.</summary>
    public static RoleMatrixRow Row(int index) => Rows[index];

    /// <summary>Every protected action, as the actions themselves describe it.</summary>
    public static IReadOnlyList<RoleMatrixRow> Rows { get; } = Build();

    private static List<RoleMatrixRow> Build()
    {
        var rows = new List<RoleMatrixRow>
        {
            // ── Auth ────────────────────────────────────────────────────────────────────────────────────
            Get("GET api/auth/me", AnySignedIn, Fixed("/api/auth/me")),
            // The account's own password change (PLAN_ADDENDUM.md 7.5): any signed-in role may call it, but the
            // allowed case must not rewrite a fixture account's password — each case signs in as a throwaway
            // user of its own instead.
            Action(HttpMethod.Post, "POST api/auth/change-password", AnySignedIn, Fixed("/api/auth/change-password"),
                _ => Task.FromResult<object>(new { currentPassword = "MatrixPw@2026", newPassword = "MatrixPw@2026x" }),
                actor: async (context, role) => await context.MatrixUserTokenAsync(role)),
            // ── Clinic settings ─────────────────────────────────────────────────────────────────────────
            Get("GET api/clinic", Staff, Fixed("/api/clinic")),
            Action(HttpMethod.Put, "PUT api/clinic", AdminOnly, Fixed("/api/clinic"),
                context => context.CurrentSettingsAsync()),
            // PLAN.md 10.7: the clinic logo upload (admin permission); the PNG upload content is the same
            // helper the visit attachments rows use.
            Action(HttpMethod.Post, "POST api/clinic/logo", AdminOnly, Fixed("/api/clinic/logo"),
                _ => Task.FromResult<object>(RoleMatrixContext.PngUploadContent())),

            // ── Dashboard ───────────────────────────────────────────────────────────────────────────────
            Get("GET api/dashboard", Staff, Fixed("/api/dashboard")),

            // ── Patients ────────────────────────────────────────────────────────────────────────────────
            Get("GET api/patients", Staff, Fixed("/api/patients")),
            Get("GET api/patients/{id:int}", Staff,
                async context => $"/api/patients/{await context.NewPatientIdAsync()}"),
            Action(HttpMethod.Post, "POST api/patients", Staff, Fixed("/api/patients"),
                _ => Task.FromResult<object>(new { fullName = "مريض مصفوفة الأدوار", phone = "01000000001" })),
            Action(HttpMethod.Put, "PUT api/patients/{id:int}", Staff,
                async context => $"/api/patients/{await context.NewPatientIdAsync()}",
                _ => Task.FromResult<object>(new { fullName = "مريض مصفوفة الأدوار (معدّل)", phone = "01000000002" })),
            Action(HttpMethod.Delete, "DELETE api/patients/{id:int}", AdminOnly,
                async context => $"/api/patients/{await context.NewThrowawayPatientIdAsync()}"),
            // PLAN_ADDENDUM.md 9.3 data rights: the export is a read (it only writes its own "Exported"
            // audit row), so the shared matrix patient file is enough; the anonymise is one-way and must
            // never touch it, so it gets a file of its own like the delete.
            Get("GET api/patients/{id:int}/export", AdminOnly,
                async context => $"/api/patients/{await context.NewPatientIdAsync()}/export"),
            Action(HttpMethod.Post, "POST api/patients/{id:int}/anonymize", AdminOnly,
                async context => $"/api/patients/{await context.NewThrowawayPatientIdAsync()}/anonymize"),

            // ── Invoices ────────────────────────────────────────────────────────────────────────────────
            Get("GET api/invoices", Staff, Fixed("/api/invoices")),
            Action(HttpMethod.Post, "POST api/invoices", Staff, Fixed("/api/invoices"),
                async context => new
                {
                    patientId = await context.NewPatientIdAsync(),
                    description = "فاتورة مصفوفة الأدوار",
                    amount = 150m,
                    discount = 0m,
                    paid = 0m,
                    method = "Cash"
                }),
            Action(HttpMethod.Post, "POST api/invoices/{id:int}/payments", Staff,
                async context => $"/api/invoices/{await context.NewInvoiceIdAsync()}/payments",
                _ => Task.FromResult<object>(new { amount = 10m })),

            // ── Queue ──────────────────────────────────────────────────────────────────────────────────
            Get("GET api/queue", Staff, Fixed("/api/queue")),
            Action(HttpMethod.Post, "POST api/queue/walk-in", Staff, Fixed("/api/queue/walk-in"),
                async context => new
                {
                    // A file of its own: the endpoint refuses a patient who already waits in today's queue, which the
                    // shared matrix patient does by the time this row runs.
                    patientId = await context.NewThrowawayPatientIdAsync(),
                    type = "Consultation",
                    isUrgent = false,
                    notes = "مصفوفة الأدوار"
                }),
            Action(HttpMethod.Post, "POST api/queue/next", Staff,
                async context =>
                {
                    // Urgent so this row is the one the endpoint calls in, whatever today's queue already holds.
                    await context.NewWaitingAppointmentAsync(isUrgent: true);
                    return "/api/queue/next";
                }),
            Action(HttpMethod.Post, "POST api/queue/{id:int}/check-in", Staff,
                async context => $"/api/queue/{(await context.NewBookedAppointmentAsync()).Id}/check-in"),
            Action(HttpMethod.Post, "POST api/queue/{id:int}/start", Staff,
                async context => $"/api/queue/{(await context.NewWaitingAppointmentAsync(false)).Id}/start"),
            Action(HttpMethod.Post, "POST api/queue/{id:int}/finish", Staff,
                async context => $"/api/queue/{(await context.NewWaitingAppointmentAsync(false)).Id}/finish"),
            Action(HttpMethod.Post, "POST api/queue/{id:int}/postpone", Staff,
                async context => $"/api/queue/{(await context.NewWaitingAppointmentAsync(false)).Id}/postpone"),
            Action(HttpMethod.Post, "POST api/queue/{id:int}/no-show", Staff,
                async context => $"/api/queue/{(await context.NewBookedAppointmentAsync()).Id}/no-show"),
            Action(HttpMethod.Post, "POST api/queue/{id:int}/urgent", Staff,
                async context => $"/api/queue/{(await context.NewWaitingAppointmentAsync(false)).Id}/urgent",
                _ => Task.FromResult<object>(new { isUrgent = true })),
            // The day's front-desk ↔ clinical team thread (PLAN.md 6.1): everyone who may see the queue may read
            // and add to it.
            Get("GET api/queue/messages", Staff, Fixed("/api/queue/messages")),
            Action(HttpMethod.Post, "POST api/queue/messages", Staff, Fixed("/api/queue/messages"),
                _ => Task.FromResult<object>(new { text = "رسالة مصفوفة الأدوار" })),

            // ── Appointments ───────────────────────────────────────────────────────────────────────────
            Get("GET api/appointments", Staff, Fixed("/api/appointments")),
            Get("GET api/appointments/slots", Staff,
                _ => Task.FromResult($"/api/appointments/slots?date={DateTime.Today.AddDays(1):yyyy-MM-dd}")),
            Action(HttpMethod.Post, "POST api/appointments", Staff, Fixed("/api/appointments"),
                async context =>
                {
                    var slot = await context.FreeSlotAsync();
                    return new
                    {
                        patientId = await context.NewPatientIdAsync(),
                        scheduledAt = $"{slot.Date:yyyy-MM-dd}T{slot.Time}:00",
                        type = "Consultation",
                        notes = "مصفوفة الأدوار"
                    };
                }),
            Action(HttpMethod.Put, "PUT api/appointments/{id:int}", Staff,
                async context => $"/api/appointments/{(await context.NewBookedAppointmentAsync()).Id}",
                async context =>
                {
                    var slot = await context.FreeSlotAsync();
                    return new
                    {
                        scheduledAt = $"{slot.Date:yyyy-MM-dd}T{slot.Time}:00",
                        type = "Consultation",
                        notes = "مصفوفة الأدوار (معدّل)"
                    };
                }),
            Action(HttpMethod.Post, "POST api/appointments/{id:int}/cancel", Staff,
                async context => $"/api/appointments/{(await context.NewBookedAppointmentAsync()).Id}/cancel"),
            Action(HttpMethod.Post, "POST api/appointments/{id:int}/link-patient", Staff,
                async context => $"/api/appointments/{(await context.NewBookedAppointmentAsync()).Id}/link-patient",
                async context => new { patientId = await context.NewPatientIdAsync() }),

            // ── Visits ─────────────────────────────────────────────────────────────────────────────────
            Get("GET api/visits/by-patient/{patientId:int}", Clinical,
                async context => $"/api/visits/by-patient/{await context.NewPatientIdAsync()}"),
            Get("GET api/visits/{id:int}/print", Clinical,
                async context => $"/api/visits/{await context.NewVisitIdAsync()}/print"),
            Action(HttpMethod.Post, "POST api/visits", Clinical, Fixed("/api/visits"),
                async context => new
                {
                    patientId = await context.NewPatientIdAsync(),
                    chiefComplaint = "شكوى مصفوفة الأدوار",
                    diagnosis = "تشخيص مصفوفة الأدوار",
                    prescription = new[] { new { drugName = "دواء مصفوفة الأدوار", dose = "1 قرص", frequency = "كل 8 ساعات" } }
                }),
            // PLAN_FULL.md 3.1 — light alert check on the visit draft (allergy / interaction / missing lab)
            Action(HttpMethod.Post, "POST api/visits/drafts/alerts", Clinical, Fixed("/api/visits/drafts/alerts"),
                async context => new
                {
                    patientId = await context.NewPatientIdAsync(),
                    diagnosis = "التهاب حاد",
                    temperature = 39.5m,
                    prescription = new[] { new { drugName = "Ibuprofen" } }
                }),
            Action(HttpMethod.Put, "PUT api/visits/{id:int}", Clinical,
                async context => $"/api/visits/{await context.NewVisitIdAsync()}",
                async context => new
                {
                    patientId = await context.NewPatientIdAsync(),
                    diagnosis = "تشخيص محدَّث لمصفوفة الأدوار",
                    prescription = new[] { new { drugName = "دواء مصفوفة الأدوار (معدَّل)", dose = "1 قرص" } }
                }),
            // PLAN_FULL.md 5.1 — lab / X-ray images on a saved visit: upload, list and open. The open row's file
            // really exists in the test attachment folder, so the allowed case always serves the row's own bytes.
            Action(HttpMethod.Post, "POST api/visits/{id:int}/attachments", Clinical,
                async context => $"/api/visits/{await context.NewVisitIdAsync()}/attachments",
                _ => Task.FromResult<object>(RoleMatrixContext.PngUploadContent())),
            Get("GET api/visits/{visitId:int}/attachments", Clinical,
                async context => $"/api/visits/{await context.NewVisitIdAsync()}/attachments"),
            Get("GET api/visits/attachments/{id:int}", Clinical,
                async context => $"/api/visits/attachments/{await context.NewAttachmentIdAsync()}"),

            // ── Users (admin only) ─────────────────────────────────────────────────────────────────────
            Get("GET api/users", AdminOnly, Fixed("/api/users")),
            Action(HttpMethod.Post, "POST api/users", AdminOnly, Fixed("/api/users"),
                context => Task.FromResult<object>(new
                {
                    fullName = "مستخدم مصفوفة الأدوار",
                    username = context.NewUsername(),
                    password = "Matrix@2026",
                    role = "Reception"
                })),
            Action(HttpMethod.Put, "PUT api/users/{id:int}", AdminOnly,
                async context => $"/api/users/{await context.NewUserIdAsync()}",
                _ => Task.FromResult<object>(new { fullName = "مستخدم مصفوفة الأدوار (معدَّل)", role = "Reception", isActive = true })),
            // PLAN.md 1.4: the per-account permission set — managed by whoever holds the admin permission
            // (the Admin role by default). The PUT stores an explicit set, proving the endpoint accepts one.
            Get("GET api/users/{id:int}/permissions", AdminOnly,
                async context => $"/api/users/{await context.NewUserIdAsync()}/permissions"),
            Action(HttpMethod.Put, "PUT api/users/{id:int}/permissions", AdminOnly,
                async context => $"/api/users/{await context.NewUserIdAsync()}/permissions",
                _ => Task.FromResult<object>(new { permissions = new[] { "queue", "patients" } })),

            Action(HttpMethod.Post, "POST api/users/{id:int}/reset-password", AdminOnly,
                async context => $"/api/users/{await context.NewUserIdAsync()}/reset-password",
                _ => Task.FromResult<object>(new { newPassword = "Matrix@2027" })),
            // PLAN.md 10.6: logical delete of a staff account — a throwaway row is consumed per case.
            Action(HttpMethod.Delete, "DELETE api/users/{id:int}", AdminOnly,
                async context => $"/api/users/{await context.NewUserIdAsync()}"),

            // ── AI assistant ───────────────────────────────────────────────────────────────────────────
            Get("GET api/ai/status", Clinical, Fixed("/api/ai/status")),
            Action(HttpMethod.Post, "POST api/ai/patients/{patientId:int}/summary", Clinical,
                async context => $"/api/ai/patients/{await context.NewPatientIdAsync()}/summary"),
            Action(HttpMethod.Post, "POST api/ai/ask", Clinical, Fixed("/api/ai/ask"),
                _ => Task.FromResult<object>(new { question = "ما هي استخدامات الميتفورمين؟" })),
            Action(HttpMethod.Post, "POST api/ai/draft", Clinical, Fixed("/api/ai/draft"),
                async context => new { patientId = await context.NewPatientIdAsync(), kind = "report" }),
            // PLAN_FULL.md 3.2 — second opinion: the differential probabilities of the diagnosis being written
            Action(HttpMethod.Post, "POST api/ai/differential", Clinical, Fixed("/api/ai/differential"),
                async context => new { patientId = await context.NewPatientIdAsync(), diagnosis = "Hypertension" }),

            // ── Medical library ────────────────────────────────────────────────────────────────────────
            // PLAN.md 1.3 + PLAN_ADDENDUM.md 7.3: the shared library is owner-managed — the list (which exposes
            // the real folder path) and every mutation are SuperAdmin only; a clinic role keeps just the cited file.
            Get("GET api/library", SuperAdminOnly, Fixed("/api/library")),
            Action(HttpMethod.Post, "POST api/library/reindex", SuperAdminOnly,
                async context =>
                {
                    // A reindex running from an earlier row would answer 409 instead of 202: wait it out first.
                    await context.WaitForLibraryIdleAsync();
                    return "/api/library/reindex";
                },
                allowedStatuses: ReindexAnswers),
            Action(HttpMethod.Delete, "DELETE api/library", SuperAdminOnly,
                async context =>
                {
                    await context.WaitForLibraryIdleAsync();
                    return $"/api/library?name={await context.CreateLibraryFileAsync()}";
                },
                allowedStatuses: DeleteAnswers),
            Action(HttpMethod.Post, "POST api/library/upload", SuperAdminOnly, Fixed("/api/library/upload"),
                _ => Task.FromResult<object>(RoleMatrixContext.UploadContent())),
            Get("GET api/library/documents/{id:int}", Clinical,
                async context => $"/api/library/documents/{await context.NewLibraryDocumentIdAsync()}"),

            // ── Audit trail (PLAN_ADDENDUM.md 7.4) ──────────────────────────────────────────────────────
            // Read-only, and a clinic admin sees only their own clinic's rows: the trail must stay a door the
            // clinic's admin can walk, not the doctor's or the reception's.
            Get("GET api/audit-logs", AdminOnly, Fixed("/api/audit-logs")),
            // The AI interaction log (PLAN_ADDENDUM.md 9.2): the same admin door, a separate store.
            Get("GET api/audit-logs/ai", AdminOnly, Fixed("/api/audit-logs/ai")),

            // ── Owner panel (PLAN.md 1.1) ───────────────────────────────────────────────────────────────
            // These rows, plus the library rows above (PLAN_ADDENDUM.md 7.3), are every row SuperAdmin may touch.
            // All the remaining rows leave it out, which keeps an owner token — a token with no clinic — away from
            // every clinic's data.
            Get("GET api/owner/clinics", SuperAdminOnly, Fixed("/api/owner/clinics")),
            Get("GET api/owner/clinics/{id:int}", SuperAdminOnly,
                context => Task.FromResult($"/api/owner/clinics/{context.ClinicId}")),
            // PLAN.md 10.1 — registering a clinic: the body hands out a fresh slug and admin username on every
            // call, so the allowed case always succeeds on its own row without touching a fixture.
            Action(HttpMethod.Post, "POST api/owner/clinics", SuperAdminOnly, Fixed("/api/owner/clinics"),
                context => Task.FromResult<object>(new
                {
                    name = "عيادة مصفوفة الأدوار",
                    slug = context.NewUsername(),
                    doctorName = "د. المصفوفة",
                    specialty = "عام",
                    adminUsername = context.NewUsername(),
                    adminPassword = "Matrix@2026",
                    consultationFee = 300m,
                    followUpFee = 200m
                })),
            // PLAN.md 10.4 — the owner's subscription controls: switch the fixture clinic's plan and book a
            // payment against it. Both answers come from the caller's own writes, so the case is self-contained.
            Action(HttpMethod.Put, "PUT api/owner/clinics/{id:int}/subscription", SuperAdminOnly,
                context => Task.FromResult($"/api/owner/clinics/{context.ClinicId}/subscription"),
                _ => Task.FromResult<object>(new { plan = "Pro", aiEnabled = true })),
            Action(HttpMethod.Post, "POST api/owner/clinics/{id:int}/payments", SuperAdminOnly,
                context => Task.FromResult($"/api/owner/clinics/{context.ClinicId}/payments"),
                _ => Task.FromResult<object>(new { amount = 500m, paidAtUtc = DateTime.UtcNow, note = "مصفوفة الأدوار" })),

            // The AI settings the owner manages (PLAN.md 1.2). The test ping always answers 200 with an ok flag —
            // even when no AI server is reachable — so it counts as "got through" for the matrix.
            // The installation-wide audit trail (PLAN_ADDENDUM.md 7.4): the owner's read-only view over every
            // clinic's rows — the only place where crossing clinics is intended.
            Get("GET api/owner/audit-logs", SuperAdminOnly, Fixed("/api/owner/audit-logs")),
            // The installation-wide AI interaction log (PLAN_ADDENDUM.md 9.2), beside its event-trail twin.
            Get("GET api/owner/audit-logs/ai", SuperAdminOnly, Fixed("/api/owner/audit-logs/ai")),
            Get("GET api/owner/ai-settings", SuperAdminOnly, Fixed("/api/owner/ai-settings")),
            Action(HttpMethod.Put, "PUT api/owner/ai-settings", SuperAdminOnly, Fixed("/api/owner/ai-settings"),
                _ => Task.FromResult<object>(new { clinicId = (int?)null, baseUrl = "http://127.0.0.1:1234/v1", apiKey = "", model = "qwen3.5-9b" })),
            Action(HttpMethod.Post, "POST api/owner/ai-settings/test", SuperAdminOnly, Fixed("/api/owner/ai-settings/test"),
                _ => Task.FromResult<object>(new { clinicId = (int?)null })),
            Action(HttpMethod.Delete, "DELETE api/owner/ai-settings/{clinicId:int}", SuperAdminOnly,
                context => Task.FromResult($"/api/owner/ai-settings/{context.ClinicId}")),

            // The clinic-level lab/radiology catalog the admin uploads from Settings (PLAN.md 10.9). The dummy
            // bytes are not a real PDF, so "فشل قراءة ملف PDF" (400) is the endpoint's own answer while opening it.
            Action(HttpMethod.Post, "POST api/clinic/catalog/upload-preview", AdminOnly,
                Fixed("/api/clinic/catalog/upload-preview?type=Lab"),
                _ =>
                {
                    var file = new ByteArrayContent(PdfContent);
                    file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                    return Task.FromResult<object>(new MultipartFormDataContent { { file, "file", "test.pdf" } });
                },
                allowedStatuses: new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest }),
            Action(HttpMethod.Post, "POST api/clinic/catalog/save", AdminOnly,
                Fixed("/api/clinic/catalog/save?type=Lab"),
                _ => Task.FromResult<object>(new { Names = new[] { "Test1", "Test2" }, Replace = true })),

            // The owner's shared catalogs (PLAN.md 10.8/10.9): the drug import and the PDF lists he keeps for
            // every clinic. BadRequest on the uploads is the endpoint's own answer to the dummy PDF bytes.
            Get("GET api/owner/catalog/items", SuperAdminOnly, Fixed("/api/owner/catalog/items")),
            Get("GET api/owner/catalog/drugs", SuperAdminOnly, Fixed("/api/owner/catalog/drugs?q=panadol")),
            Action(HttpMethod.Post, "POST api/owner/upload-catalog", SuperAdminOnly, Fixed("/api/owner/upload-catalog?type=Lab"),
                _ =>
                {
                    var file = new ByteArrayContent(PdfContent);
                    file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                    return Task.FromResult<object>(new MultipartFormDataContent { { file, "file", "test.pdf" } });
                },
                allowedStatuses: new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest }),
            Action(HttpMethod.Post, "POST api/owner/save-catalog", SuperAdminOnly, Fixed("/api/owner/save-catalog"),
                _ => Task.FromResult<object>(new { Lines = new[] { "Test1" }, Type = "Lab" })),

            // PLAN.md 10.10: the visit form reads the catalogs through api/catalog (clinical accounts only).
            Get("GET api/catalog/drugs", Clinical, Fixed("/api/catalog/drugs?q=panadol")),
            Get("GET api/catalog/items", Clinical, Fixed("/api/catalog/items")),

        };

        return rows;
    }

    private static RoleMatrixRow Get(string route, string[] roles, Func<RoleMatrixContext, Task<string>> path) =>
        Action(HttpMethod.Get, route, roles, path);

    private static RoleMatrixRow Action(HttpMethod method, string route, string[] roles,
        Func<RoleMatrixContext, Task<string>> path, Func<RoleMatrixContext, Task<object>>? body = null,
        HttpStatusCode[]? allowedStatuses = null,
        Func<RoleMatrixContext, string, Task<string>>? actor = null) =>
        new(route, roles, async context =>
        {
            var request = new HttpRequestMessage(method, await path(context));
            if (body != null)
            {
                var payload = await body(context);
                request.Content = payload is HttpContent content ? content : JsonContent.Create(payload);
            }

            return request;
        }, allowedStatuses, actor);

    /// <summary>A row whose URL never depends on data, which covers most of the table.</summary>
    private static Func<RoleMatrixContext, Task<string>> Fixed(string path) => _ => Task.FromResult(path);
}
