using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechCare.Clinic.Api.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// PLAN.md 10.7: the clinic's identity — a display name replaced by the owner/admin, and the stored name
    /// of the uploaded logo. Both optional; existing clinics fall back to Name and no logo.
    /// </summary>
    [DbContext(typeof(ClinicDbContext))]
    [Migration("20261007130000_AddClinicBranding")]
    public partial class AddClinicBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "Clinics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoPath",
                table: "Clinics",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "Clinics");

            migrationBuilder.DropColumn(
                name: "LogoPath",
                table: "Clinics");
        }
    }
}
