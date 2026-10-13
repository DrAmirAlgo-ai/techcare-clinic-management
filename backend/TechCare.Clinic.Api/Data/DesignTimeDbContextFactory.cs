using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ClinicDbContext>
{
    public ClinicDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ClinicDbContext>()
            .UseSqlite("Data Source=clinic.db")
            .Options;
        return new ClinicDbContext(options, new TenantContext());
    }
}
