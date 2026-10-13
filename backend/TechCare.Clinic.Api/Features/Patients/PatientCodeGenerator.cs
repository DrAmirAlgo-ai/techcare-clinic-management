using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;

namespace TechCare.Clinic.Api.Features.Patients;

public class PatientCodeGenerator
{
    private const int StartCode = 10001;
    private readonly ClinicDbContext _db;

    public PatientCodeGenerator(ClinicDbContext db) => _db = db;

    /// <summary>
    /// Sequential numeric file number per clinic (10001, 10002, ...). The max is taken over <b>every</b> row of the
    /// clinic — logically deleted files keep their code on disk until the retention service purges them, and the
    /// (ClinicId, Code) uniqueness covers those rows too, so the ordinary query filter would hand out a code
    /// that is still occupied.
    /// </summary>
    public async Task<string> NextAsync()
    {
        var codes = await _db.Patients.IgnoreQueryFilters()
            .Where(p => p.ClinicId == _db.CurrentClinicId)
            .Select(p => p.Code)
            .ToListAsync();
        var max = codes.Select(c => int.TryParse(c, out var n) ? n : 0).DefaultIfEmpty(StartCode - 1).Max();
        return (Math.Max(max, StartCode - 1) + 1).ToString();
    }
}
