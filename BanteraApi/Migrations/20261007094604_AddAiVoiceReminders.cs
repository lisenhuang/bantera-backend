using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAiVoiceReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AttemptAt",
                table: "ai_callbacks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "ai_callbacks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "Audio",
                table: "ai_callbacks",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Delivery",
                table: "ai_callbacks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "call");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "ai_callbacks",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Transcript",
                table: "ai_callbacks",
                type: "character varying(12000)",
                maxLength: 12000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptAt",
                table: "ai_callbacks");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "ai_callbacks");

            migrationBuilder.DropColumn(
                name: "Audio",
                table: "ai_callbacks");

            migrationBuilder.DropColumn(
                name: "Delivery",
                table: "ai_callbacks");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "ai_callbacks");

            migrationBuilder.DropColumn(
                name: "Transcript",
                table: "ai_callbacks");
        }
    }
}
