using BanteraApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260416000000_AddTranscriptionVersionAndWordTiming")]
    public partial class AddTranscriptionVersionAndWordTiming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TranscriptionVersion",
                table: "user_videos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DialogueLinesJson",
                table: "user_videos",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WordTimingJson",
                table: "user_videos",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TranscriptionVersion",
                table: "user_videos");

            migrationBuilder.DropColumn(
                name: "DialogueLinesJson",
                table: "user_videos");

            migrationBuilder.DropColumn(
                name: "WordTimingJson",
                table: "user_videos");
        }
    }
}
