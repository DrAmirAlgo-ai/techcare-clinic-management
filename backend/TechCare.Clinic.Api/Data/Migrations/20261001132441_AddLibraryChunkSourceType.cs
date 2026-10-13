using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechCare.Clinic.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryChunkSourceType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "LibraryChunks",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryChunks_SourceType",
                table: "LibraryChunks",
                column: "SourceType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LibraryChunks_SourceType",
                table: "LibraryChunks");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "LibraryChunks");
        }
    }
}
