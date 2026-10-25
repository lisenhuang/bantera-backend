using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class PracticePronunciation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PronunciationAttempts",
                table: "practice_dialogues",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PronunciationJson",
                table: "practice_dialogues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PronunciationRetryAt",
                table: "practice_dialogues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_practice_dialogues_PronunciationRetryAt_CreatedAt",
                table: "practice_dialogues",
                columns: new[] { "PronunciationRetryAt", "CreatedAt" },
                filter: "\"PronunciationJson\" IS NULL AND \"Ready\" = TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_practice_dialogues_PronunciationRetryAt_CreatedAt",
                table: "practice_dialogues");

            migrationBuilder.DropColumn(
                name: "PronunciationAttempts",
                table: "practice_dialogues");

            migrationBuilder.DropColumn(
                name: "PronunciationJson",
                table: "practice_dialogues");

            migrationBuilder.DropColumn(
                name: "PronunciationRetryAt",
                table: "practice_dialogues");
        }
    }
}
