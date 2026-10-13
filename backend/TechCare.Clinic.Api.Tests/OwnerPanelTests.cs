using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN.md 1.1 — the SuperAdmin owner as a role of its own. These tests pin the two halves that matter: the owner
/// is a real account with <b>no clinic</b> that sees every clinic through <c>api/owner</c>, and no clinic account
/// can create, promote or imitate it. The full endpoint × role denial side is the role matrix's job
/// (PLAN_ADDENDUM.md 0.3); here the point is the shape of the owner itself.
/// </summary>
[Collection(ApiCollection.Name)]
public class OwnerPanelTests
{
    /// <summary>Usernames this file creates must not collide with the seeder's or the matrix's.</summary>
    private static int _sequence = 70000;

    private readonly ApiFactory _factory;

    public OwnerPanelTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Owner_account_is_seeded_without_a_clinic()
    {
        await ClinicFixtures.ClinicAAsync(_factory);   // the clinic's own three accounts exist

        var owner = await _factory.WithServicesAsync(async services =>
            await services.GetRequiredService<ClinicDbContext>().Users
                .IgnoreQueryFilters().SingleAsync(u => u.Role == UserRole.SuperAdmin));

        Assert.Null(owner.ClinicId);
        Assert.Equal(TestEnvironment.OwnerUsername, owner.Username);
        Assert.True(owner.IsActive);
    }

    [Fact]
    public async Task Owner_login_is_accepted_and_reports_no_clinic()
    {
        await ClinicFixtures.ClinicAAsync(_factory);

        using var client = _factory.CreateClient();
        var (username, password) = TestData.Credentials(TestEnvironment.OwnerSlug, TestData.SuperAdminRole);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.True(response.IsSuccessStatusCode, $"owner login answered {(int)response.StatusCode}");

        using var me = await OwnerClientAsync();
        using var meResponse = await me.GetAsync("/api/auth/me");
        Assert.True(meResponse.IsSuccessStatusCode, $"/api/auth/me answered {(int)meResponse.StatusCode}");
        var body = await meResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(TestData.SuperAdminRole, body.GetProperty("user").GetProperty("role").GetString());
        // The API omits null properties, so "no clinic" shows up as the property simply not being there.
        if (body.TryGetProperty("clinic", out var clinic))
            Assert.Equal(JsonValueKind.Null, clinic.ValueKind);
    }

    [Fact]
    public async Task Owner_sees_every_clinic_with_that_clinic_own_numbers()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var b = await ClinicFixtures.ClinicBAsync(_factory);   // creates clinic B, its staff and its records

        // What the database really holds per clinic, measured right before the request. Clinic A is shared with the
        // other tests (the role matrix creates accounts in it), so nothing here may assume a fixed number. The panel
        // counts live files only — a logically deleted file (PLAN_ADDENDUM.md 9.3) is still a row in the database
        // but not a patient the owner sees, so the ground truth applies the same "not deleted" rule as the API.
        var expected = await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            return new
            {
                PatientsA = await db.Patients.IgnoreQueryFilters().CountAsync(p => p.ClinicId == a.ClinicId && !p.IsDeleted),
                PatientsB = await db.Patients.IgnoreQueryFilters().CountAsync(p => p.ClinicId == b.ClinicId && !p.IsDeleted),
                // PLAN.md 10.6: logically deleted accounts are not live staff, same rule as the API applies.
                StaffA = await db.Users.IgnoreQueryFilters().CountAsync(u => u.ClinicId == a.ClinicId && !u.IsDeleted),
                StaffB = await db.Users.IgnoreQueryFilters().CountAsync(u => u.ClinicId == b.ClinicId && !u.IsDeleted)
            };
        });

        // PLAN.md 10.5: the answer is an envelope now — the clinics under "items", the month's payments beside them.
        var rows = (await OwnerAsync("/api/owner/clinics")).GetProperty("items").EnumerateArray().ToList();
        var rowA = rows.Single(c => c.GetProperty("id").GetInt32() == a.ClinicId);
        var rowB = rows.Single(c => c.GetProperty("id").GetInt32() == b.ClinicId);

        Assert.Equal(TestEnvironment.ClinicAName, rowA.GetProperty("name").GetString());
        Assert.Equal(TestEnvironment.ClinicBName, rowB.GetProperty("name").GetString());
        Assert.Equal(TestEnvironment.ClinicASlug, rowA.GetProperty("slug").GetString());
        Assert.Equal(TestEnvironment.ClinicBSlug, rowB.GetProperty("slug").GetString());

        // Two clinics in the answer already proves the owner crosses clinics; the counts prove it keeps them apart.
        Assert.Equal(expected.PatientsA, rowA.GetProperty("patients").GetInt32());
        Assert.Equal(expected.PatientsB, rowB.GetProperty("patients").GetInt32());
        Assert.Equal(expected.StaffA, rowA.GetProperty("staff").GetProperty("total").GetInt32());
        Assert.Equal(expected.StaffB, rowB.GetProperty("staff").GetProperty("total").GetInt32());
        Assert.True(rowB.GetProperty("patients").GetInt32() >= 1);
    }

    [Fact]
    public async Task One_clinic_detail_lists_that_clinic_staff_and_nothing_else()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var b = await ClinicFixtures.ClinicBAsync(_factory);

        var detail = await OwnerAsync($"/api/owner/clinics/{b.ClinicId}");
        var usernames = detail.GetProperty("staff").EnumerateArray()
            .Select(s => s.GetProperty("username").GetString())
            .ToList();

        Assert.Equal(TestEnvironment.ClinicBSlug, detail.GetProperty("clinic").GetProperty("slug").GetString());
        Assert.Contains("admin-b", usernames);
        Assert.Contains("doctor-b", usernames);
        Assert.Contains("reception-b", usernames);
        Assert.DoesNotContain("admin", usernames);                       // clinic A's admin
        Assert.DoesNotContain(TestEnvironment.OwnerUsername, usernames); // the owner belongs to no clinic
        Assert.DoesNotContain(TestData.Credentials(TestEnvironment.ClinicASlug, TestData.AdminRole).Username, usernames);

        // Clinic A, asked for by id, comes back on its own too.
        var detailA = await OwnerAsync($"/api/owner/clinics/{a.ClinicId}");
        Assert.Equal(TestEnvironment.ClinicASlug, detailA.GetProperty("clinic").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task A_clinic_admin_cannot_create_or_promote_a_system_owner()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        using var admin = _factory.CreateAuthenticatedClient(
            await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole));

        using var created = await admin.PostAsJsonAsync("/api/users", new
        {
            fullName = "مالك مزعوم",
            username = $"fakeowner{Interlocked.Increment(ref _sequence)}",
            password = "FakeOwner@2026",
            role = TestData.SuperAdminRole
        });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);

        // Handing the role to an account that already exists is refused the same way.
        var users = (await admin.GetFromJsonAsync<JsonElement>("/api/users")).EnumerateArray().ToList();
        var target = users.First(u => u.GetProperty("role").GetString() != TestData.SuperAdminRole);
        using var promoted = await admin.PutAsJsonAsync($"/api/users/{target.GetProperty("id").GetInt32()}", new
        {
            fullName = target.GetProperty("fullName").GetString(),
            role = TestData.SuperAdminRole,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, promoted.StatusCode);

        // Nothing was created, and the owner is still the only account without a clinic.
        var systemOwners = await _factory.WithServicesAsync(async services =>
            await services.GetRequiredService<ClinicDbContext>().Users
                .IgnoreQueryFilters().CountAsync(u => u.Role == UserRole.SuperAdmin));
        Assert.Equal(1, systemOwners);
    }

    [Fact]
    public async Task An_account_created_by_a_clinic_admin_belongs_to_that_clinic()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        using var admin = _factory.CreateAuthenticatedClient(
            await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.AdminRole));

        var username = $"ownercheck{Interlocked.Increment(ref _sequence)}";
        using var response = await admin.PostAsJsonAsync("/api/users", new
        {
            fullName = "حساب جديد",
            username,
            password = "NewAccount@2026",
            role = TestData.ReceptionRole
        });
        Assert.True(response.IsSuccessStatusCode, $"creating a reception account answered {(int)response.StatusCode}");

        // Accounts are IClinicOptional now (the owner has no clinic), so UsersController has to stamp the clinic.
        var created = await _factory.WithServicesAsync(async services =>
            await services.GetRequiredService<ClinicDbContext>().Users
                .IgnoreQueryFilters().SingleAsync(u => u.Username == username));
        Assert.Equal(a.ClinicId, created.ClinicId);
    }

    /// <summary>
    /// PLAN_ADDENDUM.md 7.3 acceptance: the shared library's management endpoints answer 403 to every role that is
    /// not the owner (the role matrix proves the same matrix-wide), and the owner's own list keeps working.
    /// </summary>
    [Fact]
    public async Task Every_clinic_role_is_refused_on_every_library_endpoint()
    {
        await ClinicFixtures.ClinicAAsync(_factory);

        foreach (var role in TestData.AllRoles)
        {
            using var client = _factory.CreateAuthenticatedClient(
                await _factory.TokenAsync(TestEnvironment.ClinicASlug, role));
            using var list = await client.GetAsync("/api/library");
            Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
            using var reindex = await client.PostAsync("/api/library/reindex", new StringContent(""));
            Assert.Equal(HttpStatusCode.Forbidden, reindex.StatusCode);
            using var upload = await client.PostAsync("/api/library/upload", RoleMatrixContext.UploadContent());
            Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
            using var delete = await client.DeleteAsync("/api/library?name=missing.txt");
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }

        using var owner = await OwnerClientAsync();
        using var ownerList = await owner.GetAsync("/api/library");
        Assert.Equal(HttpStatusCode.OK, ownerList.StatusCode);
    }

    /// <summary>
    /// PLAN.md 1.3: a doctor opens the full file a cited answer points at — by the document's id, as the file's
    /// own bytes — while the library's management stays closed to the clinic roles and the owner's list keeps
    /// exposing the folder to the owner alone.
    /// </summary>
    [Fact]
    public async Task A_doctor_opens_a_cited_document_but_not_the_library_management()
    {
        await ClinicFixtures.ClinicAAsync(_factory);

        // The owner uploads one file, then waits for the background index to carry it in the database. The upload
        // stores the bytes under a random name (PLAN_ADDENDUM.md 7.8), so the response's fileName is what the list
        // will report — the multipart name never reaches the folder.
        using var owner = await OwnerClientAsync();
        string uploadedName;
        using (var uploaded = await owner.PostAsync("/api/library/upload", CitedFileContent()))
        {
            Assert.True(uploaded.IsSuccessStatusCode, $"owner upload answered {(int)uploaded.StatusCode}.");
            uploadedName = (await TestHttp.JsonAsync(uploaded)).GetProperty("fileName").GetString()!;
        }

        // A sweep that started before the upload cannot pick the file up, so: wait one out, and if the index still
        // has no row, run a second sweep.
        JsonElement list = default;
        for (var sweep = 0; ; sweep++)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var response = await owner.GetAsync("/api/library");
                Assert.True(response.IsSuccessStatusCode, $"owner list answered {(int)response.StatusCode}.");
                list = await TestHttp.JsonAsync(response);
                if (list.TryGetProperty("documents", out var documents)
                    && documents.EnumerateArray().Any(d => d.GetProperty("fileName").GetString() == uploadedName))
                    break;
                await Task.Delay(100);
            }

            var indexed = list.TryGetProperty("documents", out var held)
                && held.EnumerateArray().Any(d => d.GetProperty("fileName").GetString() == uploadedName);
            if (indexed) break;
            if (sweep >= 2)
                Assert.Fail($"{uploadedName} was never indexed. library state: {list.GetRawText()}");
            using var started = await owner.PostAsync("/api/library/reindex", new StringContent(""));
            if (started.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.Conflict))
                throw new InvalidOperationException($"reindex answered {(int)started.StatusCode} for the owner.");
        }

        Assert.True(list.TryGetProperty("folder", out var folder), "the owner's list exposes the folder.");
        Assert.False(string.IsNullOrEmpty(folder.GetString()));
        var documentId = list.GetProperty("documents").EnumerateArray()
            .First(d => d.GetProperty("fileName").GetString() == uploadedName)
            .GetProperty("id").GetInt32();

        using var doctor = _factory.CreateAuthenticatedClient(
            await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole));
        using var open = await doctor.GetAsync($"/api/library/documents/{documentId}");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal("text/plain", open.Content.Headers.ContentType?.MediaType);
        Assert.Contains("متن مستند المالك.", await open.Content.ReadAsStringAsync());

        // The same doctor cannot enumerate or touch the library itself.
        using var refused = await doctor.GetAsync("/api/library");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    private static MultipartFormDataContent CitedFileContent()
    {
        var content = new MultipartFormDataContent();
        // The file text stays above TextChunker's 40 character floor so the sweep actually chunks it.
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("متن مستند المالك. نص إضافي طويل يكفي لتكوين مقطع مفهرس من الملف."));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", "owner-note.txt");
        return content;
    }

    /// <summary>
    /// PLAN.md 10.1 — the owner registers a clinic: the clinic row with the installation's default fees, and the
    /// admin account that must change its temporary password at the first login. A repeated slug answers 409.
    /// </summary>
    [Fact]
    public async Task Owner_registers_a_new_clinic_with_its_admin_account()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        var slug = $"owrc{Interlocked.Increment(ref _sequence)}";
        var username = $"ownad{Interlocked.Increment(ref _sequence)}";

        using var owner = await OwnerClientAsync();
        using var response = await owner.PostAsJsonAsync("/api/owner/clinics", new
        {
            name = "عيادة التسجيل التجريبي",
            slug,
            doctorName = "د. التسجيل",
            specialty = "باطنة",
            adminUsername = username,
            adminPassword = "OwnerNew@2026"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(slug, body.GetProperty("clinic").GetProperty("slug").GetString());
        Assert.Equal(username, body.GetProperty("adminUsername").GetString());
        // The temporary password is returned once so the owner can hand it over; it never travels back in reads.
        Assert.Equal("OwnerNew@2026", body.GetProperty("adminPassword").GetString());

        var admin = await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            var clinic = await db.Clinics.SingleAsync(c => c.Slug == slug);
            // The fee fields were left out of the request, so the installation defaults (300/200) applied.
            Assert.Equal(300m, clinic.ConsultationFee);
            Assert.Equal(200m, clinic.FollowUpFee);

            var created = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Username == username);
            Assert.Equal(clinic.Id, created.ClinicId);
            Assert.Equal(UserRole.Admin, created.Role);
            Assert.True(created.IsActive);
            Assert.True(created.MustChangePassword);   // PLAN_ADDENDUM.md 7.5: the first login forces the change
            return created;
        });
        Assert.True(admin.MustChangePassword);
    }

    [Fact]
    public async Task Owner_cannot_register_a_clinic_with_a_taken_slug()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);

        using var owner = await OwnerClientAsync();
        using var response = await owner.PostAsJsonAsync("/api/owner/clinics", new
        {
            name = "عيادة مكررة",
            slug = a.Slug,
            doctorName = "د. مكرر",
            specialty = "عام",
            adminUsername = $"dup{Interlocked.Increment(ref _sequence)}",
            adminPassword = "OwnerDup@2026"
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The 409 must not have written a second clinic row under that slug.
        var rows = await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            return await db.Clinics.CountAsync(c => c.Slug == a.Slug);
        });
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Owner_cannot_register_a_clinic_with_a_taken_admin_username()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        var slug = $"dupe{Interlocked.Increment(ref _sequence)}";

        // "admin" is the seeded first clinic's account; usernames are unique across the whole installation, so a
        // new clinic (even with a fresh slug) cannot shadow it.
        using var owner = await OwnerClientAsync();
        using var response = await owner.PostAsJsonAsync("/api/owner/clinics", new
        {
            name = "عيادة اسم مكرر",
            slug,
            doctorName = "د. مكرر",
            specialty = "عام",
            adminUsername = "admin",
            adminPassword = "OwnerDup@2026"
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("اسم المستخدم", await response.Content.ReadAsStringAsync());

        // The 409 must not have written a clinic row under the fresh slug.
        var rows = await _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            return await db.Clinics.CountAsync(c => c.Slug == slug);
        });
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task Owner_registering_a_clinic_rejects_an_invalid_slug()
    {
        await ClinicFixtures.ClinicAAsync(_factory);

        using var owner = await OwnerClientAsync();
        using var response = await owner.PostAsJsonAsync("/api/owner/clinics", new
        {
            name = "عيادة",
            slug = "عيادة-عربية 1",
            doctorName = "د. صلاح",
            specialty = "",
            adminUsername = $"inval{Interlocked.Increment(ref _sequence)}",
            adminPassword = "OwnerBad@2026"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Owner_registering_a_clinic_rejects_a_weak_admin_password()
    {
        await ClinicFixtures.ClinicAAsync(_factory);

        using var owner = await OwnerClientAsync();
        using var response = await owner.PostAsJsonAsync("/api/owner/clinics", new
        {
            name = "عيادة",
            slug = $"weakpw{Interlocked.Increment(ref _sequence)}",
            doctorName = "د. صلاح",
            specialty = "",
            adminUsername = $"weakp{Interlocked.Increment(ref _sequence)}",
            adminPassword = "12345"
        });
        // The password policy refuses it before anything is written.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpClient> OwnerClientAsync()
    {
        var token = await _factory.TokenAsync(TestEnvironment.OwnerSlug, TestData.SuperAdminRole);
        return _factory.CreateAuthenticatedClient(token);
    }

    private async Task<JsonElement> OwnerAsync(string path)
    {
        using var client = await OwnerClientAsync();
        using var response = await client.GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode} for the owner.");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
