using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Features.Catalog;

/// <summary>
/// PLAN.md 10.10: the visit form reads the catalogs from here — drug-name suggestions and the
/// lab/radiology pick list. Read-only; the catalogs themselves stay maintained by the clinic admin
/// (api/clinic/catalog) and the owner (api/owner).
/// </summary>
[ApiController]
[Route("api/catalog")]
[Authorize(Policy = Permissions.Assistant)]
public class CatalogController : ControllerBase
{
    private readonly ClinicDbContext _db;

    public CatalogController(ClinicDbContext db)
    {
        _db = db;
    }

    /// <summary>Prefix-then-contains search over the drug catalog, 15 results max (PLAN.md 10.8/10.10).</summary>
    [HttpGet("drugs")]
    public async Task<IActionResult> GetDrugs([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(Array.Empty<object>());
        var term = q.Trim().ToLower();
        var startsWith = await _db.DrugCatalogs.AsNoTracking()
            .Where(d => d.TradeName.ToLower().StartsWith(term) || d.ActiveIngredient.ToLower().StartsWith(term))
            .Select(d => new { d.Id, Name = d.TradeName })
            .Take(15)
            .ToListAsync();
        if (startsWith.Count >= 15)
            return Ok(startsWith);
        var contains = await _db.DrugCatalogs.AsNoTracking()
            .Where(d => (d.TradeName.ToLower().Contains(term) || d.ActiveIngredient.ToLower().Contains(term))
                        && !(d.TradeName.ToLower().StartsWith(term) || d.ActiveIngredient.ToLower().StartsWith(term)))
            .Select(d => new { d.Id, Name = d.TradeName })
            .Take(15 - startsWith.Count)
            .ToListAsync();
        return Ok(startsWith.Concat(contains).ToList());
    }

    /// <summary>The lab/radiology names for the multi-select: this clinic's list plus the owner's shared one.</summary>
    [HttpGet("items")]
    public async Task<IActionResult> GetItems([FromQuery] string? type, [FromQuery] string? q)
    {
        var query = _db.CatalogItems.AsNoTracking().AsQueryable(); // tenant filter keeps this clinic + the shared list
        if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<CatalogItemType>(type, true, out var itemType))
            query = query.Where(ci => ci.Type == itemType);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(ci => ci.Name.ToLower().Contains(term));
        }
        var items = await query.OrderBy(ci => ci.Name).Select(ci => new { ci.Id, ci.Name }).Take(50).ToListAsync();
        return Ok(items);
    }
}
