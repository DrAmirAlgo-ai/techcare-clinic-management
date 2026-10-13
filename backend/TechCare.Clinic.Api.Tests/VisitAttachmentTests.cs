using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_FULL.md 5.1 — the lab scans and X-rays a clinic keeps with a saved visit: the content is checked by its
/// own signature, the file lands in the clinic's own folder under the server's random name, and the database
/// row (never a caller-supplied path) is the only door to the file — inside the clinic's own filter.
/// </summary>
[Collection(ApiCollection.Name)]
public class VisitAttachmentTests
{
    private readonly ApiFactory _factory;

    public VisitAttachmentTests(ApiFactory factory) => _factory = factory;

    private static MultipartFormDataContent Multipart(string fileName, byte[] bytes) =>
        new() { { new ByteArrayContent(bytes, 0, bytes.Length), "file", fileName } };

    /// <summary>A PNG byte stream the signature check accepts; the endpoint only ever reads its leading bytes.</summary>
    private static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x49, 0x44, 0x41,
        0x54, 0x28, 0xCE, 0x49, 0x00, 0x01, 0x00, 0x02
    ];

    /// <summary>A minimal PDF header the signature check accepts.</summary>
    private static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF");

    private async Task<(int ClinicId, int VisitId)> NewVisitAsync()
    {
        var context = await RoleMatrixContext.PreparedAsync(_factory);
        return (context.ClinicId, await context.NewVisitIdAsync());
    }

    [Fact]
    public async Task A_valid_png_is_stored_under_a_random_name_in_the_clinics_folder()
    {
        var (clinicId, visitId) = await NewVisitAsync();
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);
        var bytes = Png();

        var response = await client.PostAsync($"/api/visits/{visitId}/attachments", Multipart("cbc.png", bytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetInt32();

        var row = await _factory.WithClinicAsync(clinicId, db =>
            db.VisitAttachments.AsNoTracking().FirstAsync(a => a.Id == id));
        // The stored name is the server's own: no directory part, the extension is kept, and it is not the caller's.
        Assert.DoesNotContain("/", row.StoredName);
        Assert.DoesNotContain("\\", row.StoredName);
        Assert.NotEqual("cbc.png", row.StoredName);
        Assert.EndsWith(".png", row.StoredName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("cbc.png", row.OriginalName);
        Assert.Equal("image/png", row.ContentType);
        Assert.Equal(bytes.Length, row.SizeBytes);
        Assert.Equal(visitId, row.VisitId);

        var path = Path.Combine(TestEnvironment.AttachmentsPath, clinicId.ToString(), row.StoredName);
        Assert.True(File.Exists(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_valid_pdf_is_stored_and_served_as_pdf()
    {
        var (clinicId, visitId) = await NewVisitAsync();
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);
        var bytes = Pdf();

        var response = await client.PostAsync($"/api/visits/{visitId}/attachments", Multipart("lab.pdf", bytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("application/pdf", body.GetProperty("contentType").GetString());
        var id = body.GetProperty("id").GetInt32();

        var row = await _factory.WithClinicAsync(clinicId, db =>
            db.VisitAttachments.AsNoTracking().FirstAsync(a => a.Id == id));
        Assert.EndsWith(".pdf", row.StoredName, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(TestEnvironment.AttachmentsPath, clinicId.ToString(), row.StoredName)));

        using var opened = await client.GetAsync($"/api/visits/attachments/{id}");
        Assert.Equal("application/pdf", opened.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await opened.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_fake_pdf_is_refused()
    {
        var (_, visitId) = await NewVisitAsync();
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.PostAsync($"/api/visits/{visitId}/attachments",
            Multipart("report.pdf", Encoding.ASCII.GetBytes("this is not a pdf at all")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(Directory.EnumerateFiles(TestEnvironment.AttachmentsPath, "*.pdf", SearchOption.AllDirectories),
            p => File.ReadAllBytes(p).SequenceEqual(Encoding.ASCII.GetBytes("this is not a pdf at all")));
    }

    [Fact]
    public async Task A_fake_png_is_refused_before_it_reaches_the_patient_file()
    {
        var (clinicId, visitId) = await NewVisitAsync();
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.PostAsync($"/api/visits/{visitId}/attachments",
            Multipart("fake.png", Encoding.ASCII.GetBytes("this is certainly not a png file")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = await response.Content.ReadAsStringAsync();
        Assert.Contains("توقيع PNG", message);
        Assert.Equal(0, await _factory.WithClinicAsync(clinicId, db =>
            db.VisitAttachments.AsNoTracking().CountAsync(a => a.VisitId == visitId)));
    }

    [Fact]
    public async Task An_unsupported_type_is_refused()
    {
        var (_, visitId) = await NewVisitAsync();
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.PostAsync($"/api/visits/{visitId}/attachments",
            Multipart("notes.txt", Encoding.ASCII.GetBytes("تحليل مخبري مكتوب")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_hostile_file_name_never_escapes_the_clinics_folder()
    {
        var (clinicId, visitId) = await NewVisitAsync();
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.PostAsync($"/api/visits/{visitId}/attachments", Multipart("..\\..\\evil.png", Png()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var row = await _factory.WithClinicAsync(clinicId, db =>
            db.VisitAttachments.AsNoTracking().FirstAsync(a => a.Id == id));
        Assert.DoesNotContain("/", row.StoredName);
        Assert.True(File.Exists(Path.Combine(TestEnvironment.AttachmentsPath, clinicId.ToString(), row.StoredName)));
        // And the hostile path itself must not have created anything anywhere else.
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "evil.png")));
    }

    [Fact]
    public async Task The_uploaded_image_serves_back_its_own_bytes()
    {
        var (_, visitId) = await NewVisitAsync();
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);
        var bytes = Png();

        var uploaded = await client.PostAsync($"/api/visits/{visitId}/attachments", Multipart("xray.png", bytes));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        using var opened = await client.GetAsync($"/api/visits/attachments/{id}");

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal("image/png", opened.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await opened.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Another_clinics_doctor_cannot_see_or_open_the_attachment()
    {
        await TestData.EnsureClinicBAsync(_factory);
        var (_, visitId) = await NewVisitAsync();
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);
        var uploaded = await client.PostAsync($"/api/visits/{visitId}/attachments", Multipart("cbc.png", Png()));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // Clinic B sees neither the visit's list nor the attachment itself — the clinic filter fails closed.
        var bToken = await _factory.TokenAsync(TestEnvironment.ClinicBSlug, TestData.DoctorRole);
        using var bClient = _factory.CreateAuthenticatedClient(bToken);

        var list = await bClient.GetAsync($"/api/visits/{visitId}/attachments");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);

        var open = await bClient.GetAsync($"/api/visits/attachments/{id}");
        Assert.Equal(HttpStatusCode.NotFound, open.StatusCode);
    }
}
