using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class PracticeRecentRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_practice_exposures_UserId_DialogueId",
                table: "practice_exposures");

            migrationBuilder.CreateIndex(
                name: "IX_practice_exposures_UserId_DialogueId",
                table: "practice_exposures",
                columns: new[] { "UserId", "DialogueId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_practice_exposures_UserId_DialogueId",
                table: "practice_exposures");

            migrationBuilder.CreateIndex(
                name: "IX_practice_exposures_UserId_DialogueId",
                table: "practice_exposures",
                columns: new[] { "UserId", "DialogueId" },
                unique: true);
        }
    }
}
