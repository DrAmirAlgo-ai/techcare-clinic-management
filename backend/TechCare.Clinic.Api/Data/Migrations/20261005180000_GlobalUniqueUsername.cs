using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechCare.Clinic.Api.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// 7.2 reversed by product decision: usernames become unique across the whole installation (one "admin" per
    /// system, the owner included), so a bare login can never be ambiguous and no new clinic or user may shadow an
    /// existing account. Duplicate names that grew under the per-clinic era are renamed (name-1, name-2...) before
    /// the unique index goes in place.
    /// </summary>
    [DbContext(typeof(ClinicDbContext))]
    [Migration("20261005180000_GlobalUniqueUsername")]
    public partial class GlobalUniqueUsername : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Usernames become unique across the whole installation, so a bare login can never be ambiguous and a
            // new clinic can never shadow an existing account. Installations that grew duplicate names under the
            // per-clinic rule first: rename every second-and-later holder of a name to name-1, name-2... (the
            // oldest row keeps the name). Runs twice so a rename that lands on an already-existing name gets a
            // suffix of its own; passwords and everything else on the row stay untouched.
            const string dedupe =
                "UPDATE \"Users\" SET \"Username\" = \"Username\" || '-' || " +
                "(SELECT COUNT(*) FROM \"Users\" u2 WHERE u2.\"Username\" = \"Users\".\"Username\" AND u2.\"Id\" < \"Users\".\"Id\") " +
                "WHERE EXISTS (SELECT 1 FROM \"Users\" u3 WHERE u3.\"Username\" = \"Users\".\"Username\" AND u3.\"Id\" < \"Users\".\"Id\")";
            migrationBuilder.Sql(dedupe);
            migrationBuilder.Sql(dedupe);

            migrationBuilder.DropIndex(
                name: "IX_Users_ClinicId_Username",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The rename is not reversible: renamed users keep their new names on the way back.
            migrationBuilder.DropIndex(
                name: "IX_Users_Username",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ClinicId_Username",
                table: "Users",
                columns: new[] { "ClinicId", "Username" },
                unique: true);
        }
    }
}
