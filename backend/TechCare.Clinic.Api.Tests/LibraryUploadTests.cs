using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN_ADDENDUM.md 7.8 — the hardened library upload: the content is checked by its own signature, the file is
/// stored under a random name (never the caller's), only the owner may upload, and a hostile name cannot escape
/// the library folder.
/// </summary>
[Collection(ApiCollection.Name)]
public class LibraryUploadTests
{
    private readonly ApiFactory _factory;

    public LibraryUploadTests(ApiFactory factory) => _factory = factory;

    /// <summary>The owner's own client; uploads belong to the owner only.</summary>
    private async Task<HttpClient> OwnerClientAsync()
    {
        var token = await _factory.TokenAsync(TestEnvironment.OwnerSlug, TestData.SuperAdminRole);
        return _factory.CreateAuthenticatedClient(token);
    }

    private static MultipartFormDataContent Multipart(string fileName, byte[] bytes) =>
        new() { { new ByteArrayContent(bytes, 0, bytes.Length), "file", fileName } };

    /// <summary>Removes a file this class stored, so repeated test runs never pile up in the library folder.</summary>
    private static void TryDelete(string? storedName)
    {
        if (storedName is null) return;
        var path = Path.Combine(ApiFactory.LibraryPath, storedName);
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public async Task A_fake_pdf_is_refused_before_it_reaches_the_library()
    {
        using var client = await OwnerClientAsync();

        var response = await client.PostAsync("/api/library/upload",
            Multipart("fake.pdf", Encoding.ASCII.GetBytes("this is certainly not a pdf file")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Nothing with that content may have been stored.
        Assert.DoesNotContain(Directory.EnumerateFiles(ApiFactory.LibraryPath, "*", SearchOption.AllDirectories),
            f => File.ReadAllText(f) == "this is certainly not a pdf file");
    }

    [Fact]
    public async Task A_real_pdf_is_stored_under_a_random_name()
    {
        using var client = await OwnerClientAsync();
        string? stored = null;

        try
        {
            var response = await client.PostAsync("/api/library/upload",
                Multipart("guideline.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj")));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            stored = body.GetProperty("fileName").GetString();

            // The stored name is the server's own, never the caller's: no directory part, the extension is kept.
            Assert.DoesNotContain("/", stored!);
            Assert.DoesNotContain("\\", stored!);
            Assert.EndsWith(".pdf", stored, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(ApiFactory.LibraryPath, stored!)));
        }
        finally
        {
            TryDelete(stored);
        }
    }

    [Fact]
    public async Task A_hostile_file_name_never_escapes_the_library_folder()
    {
        using var client = await OwnerClientAsync();
        string? stored = null;

        try
        {
            var response = await client.PostAsync("/api/library/upload",
                Multipart("..\\..\\evil.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nhostile")));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            stored = body.GetProperty("fileName").GetString();

            Assert.DoesNotContain("/", stored!);
            Assert.EndsWith(".pdf", stored!, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(ApiFactory.LibraryPath, stored!)));
            // And the hostile path itself must not have created anything anywhere else.
            Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "evil.pdf")));
        }
        finally
        {
            TryDelete(stored);
        }
    }

    [Fact]
    public async Task A_plain_text_note_is_accepted()
    {
        using var client = await OwnerClientAsync();
        string? stored = null;

        try
        {
            var response = await client.PostAsync("/api/library/upload",
                Multipart("note.md", Encoding.UTF8.GetBytes("# ملاحظة\nنص تجريبي")));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            stored = body.GetProperty("fileName").GetString();
            Assert.EndsWith(".md", stored!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(stored);
        }
    }

    [Fact]
    public async Task A_clinic_account_cannot_upload()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.PostAsync("/api/library/upload",
            Multipart("note.md", Encoding.UTF8.GetBytes("hello")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _ = a;   // the fixture's job is only to guarantee the doctor account exists
    }
}
