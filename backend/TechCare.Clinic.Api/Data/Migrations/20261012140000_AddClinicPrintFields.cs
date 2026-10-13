using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechCare.Clinic.Api.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// PLAN.md 10.12: prescription print fields — doctor's qualifications and working hours text for the A5 prescription header.
    /// </summary>
    [DbContext(typeof(ClinicDbContext))]
    [Migration("20261012140000_AddClinicPrintFields")]
    public partial class AddClinicPrintFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Qualifications",
                table: "Clinics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkingHoursText",
                table: "Clinics",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Qualifications",
                table: "Clinics");

            migrationBuilder.DropColumn(
                name: "WorkingHoursText",
                table: "Clinics");
        }
    }
}