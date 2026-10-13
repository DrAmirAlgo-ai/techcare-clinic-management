using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechCare.Clinic.Api.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// PLAN.md 10.6: logical delete for staff accounts — IsDeleted/DeletedAtUtc. Existing accounts all default to
    /// not deleted; the clinic-facing query filter hides deleted rows while their visits and audit entries stay.
    /// </summary>
    [DbContext(typeof(ClinicDbContext))]
    [Migration("20261007090000_AddUserLogicalDelete")]
    public partial class AddUserLogicalDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "Users",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Users");
        }
    }
}
