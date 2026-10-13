using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechCare.Clinic.Api.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// PLAN.md 10.2 — the subscription fields on a clinic (plan, state, trial/renewal end dates, the per-clinic
    /// AI switch, the per-clinic trial length) and the owner's manual payment log (10.4). Existing clinics keep
    /// their full service: they predate subscriptions, so the Up path moves them to Pro with no end date, while
    /// clinics registered from the panel afterwards start on Basic in trial.
    /// </summary>
    [DbContext(typeof(ClinicDbContext))]
    [Migration("20261005150000_AddSubscriptions")]
    public partial class AddSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AiEnabled",
                table: "Clinics",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Plan",
                table: "Clinics",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubscriptionEndsAtUtc",
                table: "Clinics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubscriptionStatus",
                table: "Clinics",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TrialDays",
                table: "Clinics",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialEndsAtUtc",
                table: "Clinics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SubscriptionPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClinicId = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: false),
                    PaidAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPayments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_ClinicId",
                table: "SubscriptionPayments",
                column: "ClinicId");

            // Clinics that already exist (seeded or registered before this feature) keep working exactly as
            // they did: full service on Pro with no end date. Fresh clinics start on Basic in trial instead.
            migrationBuilder.Sql(
                "UPDATE \"Clinics\" SET \"Plan\" = 2, \"SubscriptionStatus\" = 2;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriptionPayments");

            migrationBuilder.DropColumn(
                name: "AiEnabled",
                table: "Clinics");

            migrationBuilder.DropColumn(
                name: "Plan",
                table: "Clinics");

            migrationBuilder.DropColumn(
                name: "SubscriptionEndsAtUtc",
                table: "Clinics");

            migrationBuilder.DropColumn(
                name: "SubscriptionStatus",
                table: "Clinics");

            migrationBuilder.DropColumn(
                name: "TrialDays",
                table: "Clinics");

            migrationBuilder.DropColumn(
                name: "TrialEndsAtUtc",
                table: "Clinics");
        }
    }
}
