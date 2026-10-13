using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechCare.Clinic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiInteractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LogQuestionText",
                table: "AiSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AiInteractions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClinicId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    PatientId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Sources = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    QuestionText = table.Column<string>(type: "TEXT", nullable: true),
                    AnswerText = table.Column<string>(type: "TEXT", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiInteractions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_ClinicId_AtUtc",
                table: "AiInteractions",
                columns: new[] { "ClinicId", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiInteractions");

            migrationBuilder.DropColumn(
                name: "LogQuestionText",
                table: "AiSettings");
        }
    }
}
