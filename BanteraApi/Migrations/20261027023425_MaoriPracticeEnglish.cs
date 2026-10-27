using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class MaoriPracticeEnglish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MaoriEnglishJson",
                table: "practice_dialogues",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_speaking_events_UserId_DeviceId_OccurredAt",
                table: "speaking_events",
                columns: new[] { "UserId", "DeviceId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_speaking_events_UserId_DeviceId_OccurredAt",
                table: "speaking_events");

            migrationBuilder.DropColumn(
                name: "MaoriEnglishJson",
                table: "practice_dialogues");
        }
    }
}
