using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAudioTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_audio_tests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceTestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Stage = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LanguageCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Language = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    TextModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AudioModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "text", nullable: true),
                    DialogueJson = table.Column<string>(type: "jsonb", nullable: true),
                    AudioObjectKey = table.Column<string>(type: "text", nullable: true),
                    AudioContentType = table.Column<string>(type: "text", nullable: true),
                    AudioDurationMs = table.Column<int>(type: "integer", nullable: true),
                    AudioBytes = table.Column<long>(type: "bigint", nullable: true),
                    DiagnosticsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ErrorJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audio_tests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_admin_audio_tests_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audio_tests_CreatedAt",
                table: "admin_audio_tests",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_admin_audio_tests_CreatedByUserId",
                table: "admin_audio_tests",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_admin_audio_tests_Status_CreatedAt",
                table: "admin_audio_tests",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_audio_tests");
        }
    }
}
