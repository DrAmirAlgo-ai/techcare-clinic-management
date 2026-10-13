using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechCare.Clinic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkEvidenceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Evidence",
                table: "LibraryChunks",
                type: "TEXT",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "LibraryChunks",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Evidence",
                table: "LibraryChunks");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "LibraryChunks");
        }
    }
}
