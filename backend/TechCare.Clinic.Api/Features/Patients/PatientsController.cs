using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Patients;

public record PatientRequest(
    string FullName,
    DateOnly? BirthDate,
    string? Gender,
    string? Phone,
    string? BloodType,
    string? ChronicConditions,
    string? Allergies,
    string? Notes);

[ApiController]
[Route("api/patients")]
[Authorize(Policy = Permissions.Patients)]
public class PatientsController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly PatientCodeGenerator _codes;

    public PatientsController(ClinicDbContext db, PatientCodeGenerator codes)
    {
        _db = db;
        _codes = codes;
    }

    private bool IsClinical => User.IsInRole(nameof(UserRole.Doctor)) || User.IsInRole(nameof(UserRole.Admin));

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int take = 50)
    {
        var query = _db.Patients.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var s = q.Trim();
            query = query.Where(p => p.FullName.Contains(s) || p.Code == s || (p.Phone != null && p.Phone.Contains(s)));
        }

        var patients = await query
            .OrderByDescending(p => p.Id)
            .Take(Math.Clamp(take, 1, 200))
            .Select(p => new
            {
                p.Id, p.Code, p.FullName, p.Phone, p.Gender, p.BirthDate,
                VisitsCount = p.Visits.Count,
                LastVisit = p.Visits.Max(v => (DateTime?)v.VisitDate)
            })
            .ToListAsync();

        return Ok(patients);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var p = await _db.Patients.FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();

        _db.LogRead(nameof(Patient), p.Id);
        await _db.SaveChangesAsync();

        var basic = new
        {
            p.Id, p.Code, p.FullName, p.BirthDate, p.Gender, p.Phone, p.CreatedAt, p.IsAnonymized,
            Age = CalculateAge(p.BirthDate)
        };
        if (!IsClinical) return Ok(basic);

        return Ok(new
        {
            basic.Id, basic.Code, basic.FullName, basic.BirthDate, basic.Gender, basic.Phone, basic.CreatedAt, basic.Age,
            basic.IsAnonymized,
            p.BloodType, p.ChronicConditions, p.Allergies, p.Notes
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(PatientRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.FullName))
            return BadRequest(new { message = "اسم المريض مطلوب" });

        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _db.CurrentClinicId);
        var patient = new Patient { Code = await _codes.NextAsync(), CreatedAt = ClinicClock.Now(clinic) };
        Apply(patient, r, IsClinical);
        _db.Patients.Add(patient);
        await _db.SaveChangesAsync();
        return Ok(new { patient.Id, patient.Code });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, PatientRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.FullName))
            return BadRequest(new { message = "اسم المريض مطلوب" });

        var patient = await _db.Patients.FirstOrDefaultAsync(x => x.Id == id);
        if (patient == null) return NotFound();

        Apply(patient, r, IsClinical);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// PLAN_ADDENDUM.md 9.3: deletion is logical — the file vanishes from every query (the global filter reads
    /// <c>IsDeleted</c>) while the row and its medical history stay on disk until the clinic's retention period
    /// has elapsed, when the retention service removes them physically. The event joins the audit trail twice:
    /// as the ordinary "Modified" row and as this explicit "SoftDeleted" marker.
    /// </summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permissions.Admin)]
    public async Task<IActionResult> Delete(int id)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(x => x.Id == id);
        if (patient == null) return NotFound();

        patient.IsDeleted = true;
        patient.DeletedAtUtc = DateTime.UtcNow;
        _db.LogAction("SoftDeleted", "Patient", patient.Id);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// PLAN_ADDENDUM.md 9.3: hides the identity for good — name and contact fields become an anonymous marker
    /// while the medical history stays exactly where it is. One-way: a second call is a 409, and no endpoint
    /// restores the original values. Both the ordinary "Modified" row and this explicit "Anonymized" marker
    /// join the audit trail.
    /// </summary>
    [HttpPost("{id:int}/anonymize")]
    [Authorize(Policy = Permissions.Admin)]
    public async Task<IActionResult> Anonymize(int id)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(x => x.Id == id);
        if (patient == null) return NotFound();
        if (patient.IsAnonymized)
            return Conflict(new { message = "هوية هذا الملف مخفية بالفعل ولا يمكن التراجع عنها" });

        patient.FullName = "مريض مجهول";
        patient.Phone = null;
        patient.BirthDate = null;
        patient.Gender = null;
        patient.IsAnonymized = true;
        _db.LogAction("Anonymized", "Patient", patient.Id);
        await _db.SaveChangesAsync();

        return Ok(new { patient.Id, patient.Code, patient.FullName, patient.IsAnonymized });
    }

    /// <summary>
    /// PLAN_ADDENDUM.md 9.3: the complete patient file as one structured document — demographics, every
    /// consultation with its prescription and attached image names, the bookings, and the invoices. The JSON
    /// download and the printable PDF view in the UI both render this single payload, and every fetch joins
    /// the audit trail as an "Exported" event.
    /// </summary>
    [HttpGet("{id:int}/export")]
    [Authorize(Policy = Permissions.Admin)]
    public async Task<IActionResult> Export(int id)
    {
        var patient = await _db.Patients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (patient == null) return NotFound();

        var visits = await _db.Visits.AsNoTracking()
            .Include(v => v.Prescription)
            .Where(v => v.PatientId == id)
            .OrderByDescending(v => v.VisitDate)
            .ToListAsync();
        var doctorNames = await _db.Users.AsNoTracking()
            .Where(u => visits.Select(v => v.DoctorId).Distinct().Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var visitIds = visits.Select(v => v.Id).ToList();
        var attachmentNames = await _db.VisitAttachments.AsNoTracking()
            .Where(a => visitIds.Contains(a.VisitId))
            .GroupBy(a => a.VisitId)
            .Select(g => new { VisitId = g.Key, Names = g.Select(a => a.OriginalName).ToList() })
            .ToListAsync();

        var appointments = await _db.Appointments.AsNoTracking()
            .Where(a => a.PatientId == id)
            .OrderByDescending(a => a.ScheduledAt)
            .ToListAsync();

        var invoices = await _db.Invoices.AsNoTracking()
            .Where(i => i.PatientId == id)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

        var clinic = await _db.Clinics.FirstAsync(c => c.Id == _db.CurrentClinicId);

        _db.LogAction("Exported", "Patient", patient.Id);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            exportedAtUtc = DateTime.UtcNow,
            clinic = new { clinic.Name, clinic.DoctorName, clinic.Specialty, clinic.Phone, clinic.Address,
                // PLAN.md 10.7: the exported file's printout carries the clinic's own display name.
                displayName = clinic.DisplayName ?? clinic.Name, hasLogo = clinic.LogoPath != null, clinic.Slug },
            patient = new
            {
                patient.Code, patient.FullName, patient.BirthDate, patient.Gender, patient.Phone,
                patient.BloodType, patient.ChronicConditions, patient.Allergies, patient.Notes,
                patient.CreatedAt, patient.IsAnonymized
            },
            visits = visits.Select(v => new
            {
                v.Id, v.VisitDate,
                DoctorName = doctorNames.TryGetValue(v.DoctorId, out var name) ? name : null,
                v.ChiefComplaint, v.Examination, v.Diagnosis, v.Notes, v.LabRequests, v.FollowUpDate,
                v.BloodPressure, v.Pulse, v.Temperature, v.WeightKg, v.HeightCm, v.BloodSugar,
                Prescription = v.Prescription.Select(i => new { i.DrugName, i.Dose, i.Frequency, i.Duration, i.Instructions }),
                AttachmentNames = attachmentNames.FirstOrDefault(a => a.VisitId == v.Id)?.Names ?? new List<string>()
            }),
            appointments = appointments.Select(a => new
            {
                a.Id, a.ScheduledAt, a.Type, a.Status, a.Source, a.IsUrgent, a.Notes
            }),
            invoices = invoices.Select(i => new
            {
                i.Id, i.Description, i.Amount, i.Discount, i.Paid, i.Method, i.CreatedAt,
                Net = i.Net, Remaining = i.Remaining
            })
        });
    }

    /// <summary>Reception can register and edit contact details but never overwrites medical fields.</summary>
    private static void Apply(Patient p, PatientRequest r, bool includeMedical)
    {
        p.FullName = r.FullName.Trim();
        p.BirthDate = r.BirthDate;
        p.Gender = r.Gender;
        p.Phone = r.Phone?.Trim();
        if (!includeMedical) return;
        p.BloodType = r.BloodType;
        p.ChronicConditions = r.ChronicConditions;
        p.Allergies = r.Allergies;
        p.Notes = r.Notes;
    }

    private static int? CalculateAge(DateOnly? birthDate)
    {
        if (birthDate == null) return null;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var age = today.Year - birthDate.Value.Year;
        if (birthDate.Value > today.AddYears(-age)) age--;
        return age;
    }
}
