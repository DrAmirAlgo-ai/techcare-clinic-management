using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Ai;
using TechCare.Clinic.Api.Infrastructure;
using TechCare.Clinic.Api.Tests.Infrastructure;

namespace TechCare.Clinic.Api.Tests;

/// <summary>
/// PLAN.md المرحلة 2 — the retrieval engine: the question's kind orders the source kinds (2.1), pubmed studies
/// rank by evidence tier then recency (2.2), every source kind keeps its own similarity bar (2.3), an Egyptian
/// brand name reaches the label's section before any semantic search (2.4), and the prompt rules of 2.5, 2.6 and
/// 2.7. Vectors are planted at a known cosine distance from the fake embedder's deterministic query vector, so
/// every score is known before the search runs.
/// </summary>
[Collection(ApiCollection.Name)]
public class RetrievalTests
{
    private const int Dims = 768;

    private readonly ApiFactory _factory;

    public RetrievalTests(ApiFactory factory) => _factory = factory;

    // ── Controlled vectors: the fake embedder's query vector is deterministic (same hash math as
    //    FakeLmStudioClient), so a chunk can be planted at an exact cosine distance from the question.

    private static float[] FakeQueryVector(string text)
    {
        var vector = new float[Dims];
        var seed = 17u;
        foreach (var c in text) seed = seed * 31 + c;
        seed = seed * 31 + 7u;   // isQuery: true, exactly as FakeLmStudioClient does
        for (var i = 0; i < Dims; i++)
        {
            seed = seed * 1664525 + 1013904223;
            vector[i] = (seed >> 8) % 1000 / 1000f - 0.5f;
        }

        vector[0] += 1f;
        Normalize(vector);
        return vector;
    }

    /// <summary>A unit vector whose cosine with <paramref name="queryText"/>'s query embedding is <paramref name="cosine"/>.</summary>
    private static byte[] ChunkVectorAt(string queryText, double cosine)
    {
        var q = FakeQueryVector(queryText);
        var noise = new float[Dims];
        var rng = new Random(queryText.Length * 31 + (int)(cosine * 1000));
        for (var i = 0; i < Dims; i++) noise[i] = (float)(rng.NextDouble() * 2 - 1);

        var overlap = 0.0;
        for (var i = 0; i < Dims; i++) overlap += noise[i] * q[i];
        for (var i = 0; i < Dims; i++) noise[i] -= (float)(overlap * q[i]);
        var norm = Math.Sqrt(noise.Sum(x => (double)x * x));
        if (norm < 1e-9) noise[0] = 1;

        var vector = new float[Dims];
        var tail = Math.Sqrt(Math.Max(0, 1 - cosine * cosine));
        var fNorm = (float)norm;
        for (var i = 0; i < Dims; i++)
        {
            noise[i] /= fNorm;
            vector[i] = (float)(cosine * q[i] + tail * noise[i]);
        }

        return LibraryVectorCache.ToBytes(vector);
    }

    private static void Normalize(float[] v)
    {
        var n = Math.Sqrt(v.Sum(x => (double)x * x));
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
    }

    // ── Fixtures

    /// <summary>Plants a library document into the test database; forgets the shared caches so this test's chunks are what gets searched.</summary>
    private Task<int> SeedLibraryAsync(int clinicId, string docName, string sourceType,
        params (string Text, string? ChunkTitle, string? Section, double Cosine, string Query, string? Evidence, int? Year)[] chunks)
    {
        return _factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ClinicDbContext>();
            services.GetRequiredService<TenantContext>().SetClinic(clinicId);

            var doc = new LibraryDocument
            {
                FileName = docName,
                Title = docName,
                FileSize = 1,
                FileModifiedUtc = DateTime.UtcNow,
                ChunkCount = chunks.Length
            };
            foreach (var c in chunks)
                doc.Chunks.Add(new LibraryChunk
                {
                    Title = c.ChunkTitle,
                    Section = c.Section,
                    SourceType = sourceType,
                    Text = c.Text,
                    Evidence = c.Evidence,
                    Year = c.Year,
                    Embedding = ChunkVectorAt(c.Query, c.Cosine)
                });
            db.LibraryDocuments.Add(doc);
            await db.SaveChangesAsync();

            services.GetRequiredService<LibraryVectorCache>().Invalidate();
            services.GetRequiredService<PharmacyBrandMap>().Invalidate();
            return 0;
        });
    }

    private Task<List<LibraryHit>> SearchAsync(int clinicId, QueryPlan plan) =>
        _factory.WithServicesAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().SetClinic(clinicId);
            return await services.GetRequiredService<MedicalLibraryService>()
                .SearchAsync(plan, Array.Empty<BrandMatch>(), CancellationToken.None);
        });

    private static QueryPlan Plan(QueryKind kind, string query, int topK) =>
        new(kind, query, "", QueryRouter.KnownSourceTypes, Array.Empty<string>(), topK, false);

    private async Task<HttpClient> DoctorClientAsync()
    {
        await ClinicFixtures.ClinicAAsync(_factory);
        var token = await _factory.TokenAsync(TestEnvironment.ClinicASlug, TestData.DoctorRole);
        return _factory.CreateAuthenticatedClient(token);
    }

    // ── 2.1: the question's kind orders the source kinds; nothing is dropped ─────────────────────────

    [Fact]
    public async Task A_drug_question_lets_the_label_lead_even_with_a_lower_raw_score()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        const string q = "ibuprofen dosage osteoarthritis";

        await SeedLibraryAsync(a.ClinicId, "r21-labels.jsonl", "drug_label",
            ("Label: DICLOFENAC. DOSAGE AND ADMINISTRATION.", "Diclofenac Sodium (Analgesic) - Egypt: فولتارين", "Dosage and Administration", 0.88, q, null, null));
        await SeedLibraryAsync(a.ClinicId, "r21-catalog.jsonl", "egypt_catalog",
            ("Ibuprofen - Egyptian brands.", "Ibuprofen - Egypt: دوليبران", null, 0.92, q, null, null));
        await SeedLibraryAsync(a.ClinicId, "r21-guideline.jsonl", "guideline",
            ("Osteoarthritis: NSAIDs may be used short term.", "Osteoarthritis Guideline", "Overview", 0.90, q, null, null));
        await SeedLibraryAsync(a.ClinicId, "r21-uploaded.txt", "uploaded",
            ("A clinic note about ibuprofen counselling.", "Clinic note", null, 0.85, q, null, null));
        await SeedLibraryAsync(a.ClinicId, "r21-pubmed.jsonl", "pubmed",
            ("A large trial of ibuprofen in osteoarthritis.", "Ibuprofen trial", "Abstract", 0.95, q, "RCT", 2018));

        var hits = await SearchAsync(a.ClinicId, Plan(QueryKind.Drug, q, 5));

        // Raw scores: pubmed 0.95 > egypt 0.92 > guideline 0.90 > label 0.88 — but the drug question weighs the
        // label at 1.0, so it still leads, and every source kind was searched (pubmed made the cut at the end).
        Assert.Equal(new[] { "drug_label", "egypt_catalog", "guideline", "uploaded", "pubmed" },
            hits.Select(h => h.SourceType).ToArray());
    }

    // ── 2.2: pubmed ranks by evidence tier, then by the newest year ─────────────────────────────────

    [Fact]
    public async Task PubMed_studies_rank_by_evidence_tier_then_by_the_newest_year()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        const string q = "statins cardiovascular risk evidence";

        await SeedLibraryAsync(a.ClinicId, "r22-a.jsonl", "pubmed",
            ("Meta-analysis of statins in primary prevention.", "Statins meta-analysis", "Abstract", 0.80, q, "Meta-analysis", 2015));
        await SeedLibraryAsync(a.ClinicId, "r22-b.jsonl", "pubmed",
            ("Randomized trial of atorvastatin.", "Atorvastatin RCT", "Abstract", 0.92, q, "RCT", 2024));
        await SeedLibraryAsync(a.ClinicId, "r22-c.jsonl", "pubmed",
            ("Systematic review of statin efficacy.", "Statins review", "Abstract", 0.85, q, "Systematic review", 2020));

        var hits = await SearchAsync(a.ClinicId, Plan(QueryKind.Research, q, 4));

        // The tier beats the raw score: the 2015 meta-analysis (0.80) leads the 2024 RCT (0.92).
        Assert.Equal(new[] { "Meta-analysis", "Systematic review", "RCT" }, hits.Select(h => h.Evidence).ToArray());

        // Same tier, different years: the newer one wins.
        await SeedLibraryAsync(a.ClinicId, "r22-d.jsonl", "pubmed",
            ("Randomized trial of rosuvastatin.", "Rosuvastatin RCT", "Abstract", 0.90, q, "RCT", 2010));
        var hits2 = await SearchAsync(a.ClinicId, Plan(QueryKind.Research, q, 4));
        var rct = hits2.Where(h => h.Evidence == "RCT").ToList();
        Assert.Equal(2024, rct[0].Year);
    }

    // ── 2.3: every source kind keeps its own similarity bar ────────────────────────────────────────

    [Fact]
    public async Task Each_source_kind_keeps_its_own_similarity_bar()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        const string q = "amoxicillin clavulanate dose renal impairment";
        const double near = 0.72;   // above the core bar (0.70), below the global one (0.75)

        await SeedLibraryAsync(a.ClinicId, "r23-label.jsonl", "drug_label",
            ("AMOXICILLIN CLAVULANATE. DOSAGE AND ADMINISTRATION.", "Amoxicillin Clavulanate", "Dosage and Administration", near, q, null, null));
        await SeedLibraryAsync(a.ClinicId, "r23-guideline.jsonl", "guideline",
            ("Community-acquired pneumonia: first-line beta-lactams.", "Pneumonia guideline", "Treatment", near, q, null, null));
        await SeedLibraryAsync(a.ClinicId, "r23-uploaded.txt", "uploaded",
            ("A clinic note on antibiotics.", "Clinic note", null, near, q, null, null));
        await SeedLibraryAsync(a.ClinicId, "r23-pubmed.jsonl", "pubmed",
            ("An abstract about antibiotic stewardship.", "Antibiotics study", "Abstract", near, q, "Observational study", 2019));
        await SeedLibraryAsync(a.ClinicId, "r23-catalog.jsonl", "egypt_catalog",
            ("Amoxicillin Clavulanate - Egyptian brands.", "Amoxicillin Clavulanate - Egypt: أوجمنتين", null, near, q, null, null));

        var hits = await SearchAsync(a.ClinicId, Plan(QueryKind.Drug, q, 8));

        var types = hits.Select(h => h.SourceType).ToHashSet();
        Assert.Contains("drug_label", types);
        Assert.Contains("guideline", types);
        Assert.Contains("uploaded", types);
        Assert.DoesNotContain("pubmed", types);          // 0.72 does not clear the pubmed bar of 0.75
        Assert.DoesNotContain("egypt_catalog", types);
    }

    // ── 2.4: an Egyptian brand name reaches the label's section before the semantic pass ───────────

    [Fact]
    public async Task An_Egyptian_brand_name_reaches_the_label_section_directly()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        const string q = "ما الجرعة المناسبة من دواء دوليبران؟";

        // The catalogue loader only reads documents whose name ends with "drugs.jsonl" — the seed file keeps that.
        await SeedLibraryAsync(a.ClinicId, "r24-drugs.jsonl", "egypt_catalog",
            ("Title: Ibuprofen - Egypt: دوليبران، نوريثروبين\nالأسماء التجارية في مصر: دوليبران، نوريثروبين",
                "Ibuprofen - Egypt: دوليبران، نوريثروبين", null, 0.05, q, null, null));
        await SeedLibraryAsync(a.ClinicId, "r24-labels.jsonl", "drug_label",
            ("Title: Ibuprofen - Egypt: دوليبران\nDOSAGE AND ADMINISTRATION: The usual adult dose is 400 mg every 8 hours.",
                "Ibuprofen (Analgesic, Pain Relief) - Egypt: دوليبران", "Dosage and Administration، Drug Interactions", 0.05, q, null, null));

        using var client = await DoctorClientAsync();
        using var response = await client.PostAsJsonAsync("/api/ai/ask", new { question = q });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await TestHttp.JsonAsync(response);
        var sources = body.GetProperty("sources").EnumerateArray().ToList();
        Assert.True(sources.Count > 0);
        var first = sources[0];
        Assert.Equal("drug_label", first.GetProperty("sourceType").GetString());
        Assert.Contains("Dosage", first.GetProperty("section").GetString());
        // The hit came from the exact-title pass, not from the (near-zero) embedding distance.
        Assert.True(first.GetProperty("score").GetDouble() > 0.9);
    }

    // ── 2.5: no fixed headings, the dose stays anchored to a cited label/guideline, the full budget applies

    [Fact]
    public async Task The_answer_prompt_has_no_fixed_headings_and_keeps_the_dose_rule()
    {
        using var client = await DoctorClientAsync();

        using var response = await client.PostAsJsonAsync("/api/ai/ask",
            new { question = "ما الجرعة المعتادة من الباراسيتامول للبالغين؟" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = await TestHttp.JsonAsync(response);

        var fake = _factory.Services.GetRequiredService<LmStudioClient>() as FakeLmStudioClient;
        var prompt = fake!.Prompts.Last().Last().Content;

        Assert.DoesNotContain("العناوين الثلاثة", prompt);
        Assert.DoesNotContain("القرار السريري", prompt);
        Assert.Contains("قواعد الإجابة", prompt);
        Assert.Contains("drug_label", prompt);
        // The answer call no longer caps the model at 700 tokens; the option's 1200 applies.
        Assert.Null(fake.LastMaxTokens);
    }

    // ── 2.6: the patient file enters the prompt as explicit constraints ────────────────────────────

    [Fact]
    public async Task The_patient_file_enters_the_prompt_as_explicit_constraints()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var patient = await _factory.WithClinicAsync(a.ClinicId, async db =>
        {
            var p = new Patient
            {
                Code = "40001",
                FullName = "مريض القيود",
                Phone = "01200000044",
                Gender = "ذكر",
                Allergies = "Penicillin",
                ChronicConditions = "ارتفاع ضغط الدم، سكر نوع 2",
                CreatedAt = DateTime.UtcNow
            };
            db.Patients.Add(p);
            var doctor = await db.Users.FirstAsync(u => u.Role == UserRole.Doctor);
            var visit = new Visit
            {
                Patient = p,   // the patient is not saved yet: the navigation keeps the foreign key honest
                DoctorId = doctor.Id,
                VisitDate = DateTime.UtcNow,
                ChiefComplaint = "متابعة",
                Diagnosis = "Hypertension"
            };
            visit.Prescription.Add(new PrescriptionItem { DrugName = "Metformin 500mg", Dose = "1 قرص", Frequency = "مرتين يومياً" });
            db.Visits.Add(visit);
            await db.SaveChangesAsync();
            return p;
        });

        using var client = await DoctorClientAsync();
        using var response = await client.PostAsJsonAsync("/api/ai/ask", new { question = "ما جرعة بروفين؟", patientId = patient.Id });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = await TestHttp.JsonAsync(response);

        var fake = _factory.Services.GetRequiredService<LmStudioClient>() as FakeLmStudioClient;
        var prompt = fake!.Prompts.Last().Last().Content;

        Assert.Contains("قيود ملف المريض", prompt);
        Assert.Contains("Penicillin", prompt);
        Assert.Contains("سكر نوع 2", prompt);
        Assert.Contains("Metformin 500mg", prompt);
        Assert.Contains("افحص التداخلات", prompt);
    }

    // ── 2.7: report/referral drafts cite a real library reference for the diagnosis ────────────────

    [Fact]
    public async Task A_report_draft_cites_the_library_reference_for_the_diagnosis()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        const string diagnosis = "Chronic kidney disease stage 3";
        var patient = await _factory.WithClinicAsync(a.ClinicId, async db =>
        {
            var p = new Patient
            {
                Code = "40002",
                FullName = "مريض التقرير",
                Phone = "01200000045",
                Gender = "أنثى",
                CreatedAt = DateTime.UtcNow
            };
            db.Patients.Add(p);
            var doctor = await db.Users.FirstAsync(u => u.Role == UserRole.Doctor);
            db.Visits.Add(new Visit
            {
                Patient = p,   // the patient is not saved yet: the navigation keeps the foreign key honest
                DoctorId = doctor.Id,
                VisitDate = DateTime.UtcNow,
                ChiefComplaint = "متابعة كلى",
                Diagnosis = diagnosis
            });
            await db.SaveChangesAsync();
            return p;
        });

        await SeedLibraryAsync(a.ClinicId, "r27-guideline.jsonl", "guideline",
            ("CKD stage 3: monitor eGFR every three months and review nephrotoxic drugs.",
                "CKD Management Guideline", "Overview", 0.80, diagnosis, null, null));

        using var client = await DoctorClientAsync();
        using var response = await client.PostAsJsonAsync("/api/ai/draft", new { patientId = patient.Id, kind = "report" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = await TestHttp.JsonAsync(response);

        var fake = _factory.Services.GetRequiredService<LmStudioClient>() as FakeLmStudioClient;
        var prompt = fake!.Prompts.Last().Last().Content;

        Assert.Contains("مرجع من مكتبة العيادة", prompt);
        Assert.Contains("CKD Management Guideline", prompt);
    }

    // ── 3.2: the second opinion next to the diagnosis — differential probabilities grounded on the
    //    2.1/2.2 library retrieval, with the patient's constraints of 2.6 checked on every candidate ──

    [Fact]
    public async Task A_second_opinion_returns_candidates_with_library_evidence_and_patient_constraints()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        const string diagnosis = "Hypertension";
        var patient = await _factory.WithClinicAsync(a.ClinicId, async db =>
        {
            var p = new Patient
            {
                Code = "40003",
                FullName = "مريض الرأي الثاني",
                Phone = "01200000046",
                Gender = "ذكر",
                Allergies = "Penicillin",
                ChronicConditions = "سكر نوع 2",
                CreatedAt = DateTime.UtcNow
            };
            db.Patients.Add(p);
            var doctor = await db.Users.FirstAsync(u => u.Role == UserRole.Doctor);
            var visit = new Visit
            {
                Patient = p,   // the patient is not saved yet: the navigation keeps the foreign key honest
                DoctorId = doctor.Id,
                VisitDate = DateTime.UtcNow,
                ChiefComplaint = "صداع وضغط مرتفع",
                Diagnosis = diagnosis
            };
            visit.Prescription.Add(new PrescriptionItem { DrugName = "Metformin 500mg", Dose = "1 قرص", Frequency = "مرتين يومياً" });
            db.Visits.Add(visit);
            await db.SaveChangesAsync();
            return p;
        });

        await SeedLibraryAsync(a.ClinicId, "r32-guideline.jsonl", "guideline",
            ("Essential hypertension: treat with ACE inhibitors and exclude secondary causes in young patients.",
                "Hypertension Guideline", "Overview", 0.80, diagnosis, null, null));

        var fake = _factory.Services.GetRequiredService<LmStudioClient>() as FakeLmStudioClient;
        var originalAnswer = fake!.Answer;
        fake.Answer = """
            {"candidates":[
              {"diagnosis":"ارتفاع ضغط أساسي","probability":70,"evidence":"[1] الدليل يصنف الحالة كارتفاع ضغط أساسي","warnings":["راجع الكلى قبل أي دواء مسكن"]},
              {"diagnosis":"ارتفاع ضغط ثانوي","probability":15,"evidence":"يُستبعد عادةً في الحالات الشابة","warnings":[]},
              {"diagnosis":"ارتفاع مؤقت بالضغط","probability":10,"evidence":"خارج المقتطفات","warnings":[]}
            ],"note":"يُفضَل تحليل وظائف الكلى"}
            """;

        using var client = await DoctorClientAsync();
        using var response = await client.PostAsJsonAsync("/api/ai/differential",
            new { patientId = patient.Id, diagnosis, chiefComplaint = "صداع وضغط مرتفع" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await TestHttp.JsonAsync(response);
        var candidates = body.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(3, candidates.Count);
        Assert.Equal("ارتفاع ضغط أساسي", candidates[0].GetProperty("diagnosis").GetString());
        Assert.Equal(70, candidates[0].GetProperty("probability").GetInt32());
        Assert.Single(candidates[0].GetProperty("warnings").EnumerateArray().ToList());
        Assert.Equal("يُفضَل تحليل وظائف الكلى", body.GetProperty("note").GetString());
        Assert.True(body.GetProperty("foundInLibrary").GetBoolean());
        var sources = body.GetProperty("sources").EnumerateArray().ToList();
        Assert.Single(sources);
        Assert.Equal(JsonValueKind.Number, sources[0].GetProperty("documentId").ValueKind);

        var prompt = fake.Prompts.Last().Last().Content;
        Assert.Contains("التشخيص الذي يكتبه الطبيب الآن", prompt);
        Assert.Contains("قيود ملف المريض", prompt);
        Assert.Contains("Penicillin", prompt);
        Assert.Contains("Hypertension Guideline", prompt);
        fake.Answer = originalAnswer;
    }

    [Fact]
    public async Task A_second_opinion_requires_a_diagnosis_and_a_known_patient()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var patient = await _factory.WithClinicAsync(a.ClinicId, async db =>
        {
            var p = new Patient { Code = "40004", FullName = "مريض التحقق", Phone = "01200000047", CreatedAt = DateTime.UtcNow };
            db.Patients.Add(p);
            await db.SaveChangesAsync();
            return p;
        });

        using var client = await DoctorClientAsync();

        using var emptyDiagnosis = await client.PostAsJsonAsync("/api/ai/differential",
            new { patientId = patient.Id, diagnosis = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, emptyDiagnosis.StatusCode);

        using var unknownPatient = await client.PostAsJsonAsync("/api/ai/differential",
            new { patientId = 999999, diagnosis = "Hypertension" });
        Assert.Equal(HttpStatusCode.NotFound, unknownPatient.StatusCode);
    }

    [Fact]
    public async Task A_second_opinion_tolerates_a_non_json_model_answer()
    {
        var a = await ClinicFixtures.ClinicAAsync(_factory);
        var patient = await _factory.WithClinicAsync(a.ClinicId, async db =>
        {
            var p = new Patient { Code = "40005", FullName = "مريض الرد غير منظم", Phone = "01200000048", CreatedAt = DateTime.UtcNow };
            db.Patients.Add(p);
            await db.SaveChangesAsync();
            return p;
        });

        var fake = _factory.Services.GetRequiredService<LmStudioClient>() as FakeLmStudioClient;
        var originalAnswer = fake!.Answer;
        fake.Answer = "عذراً، لا أستطيع إخراج JSON الآن.";

        using var client = await DoctorClientAsync();
        using var response = await client.PostAsJsonAsync("/api/ai/differential",
            new { patientId = patient.Id, diagnosis = "Hypertension" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await TestHttp.JsonAsync(response);
        Assert.Equal(0, body.GetProperty("candidates").GetArrayLength());
        // The app's MVC JSON options drop null properties (Program.cs), so a missing note arrives absent, not null.
        Assert.True(!body.TryGetProperty("note", out var note) || note.ValueKind == JsonValueKind.Null);
        fake.Answer = originalAnswer;
    }
}
