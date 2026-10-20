using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddQuickPractice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "practice_dialogues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    Language = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Topic = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MeaningKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Cue = table.Column<string>(type: "text", nullable: false),
                    Reply = table.Column<string>(type: "text", nullable: false),
                    Distractor = table.Column<string>(type: "text", nullable: false),
                    Explanation = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CueAudio = table.Column<string>(type: "text", nullable: false),
                    ReplyAudio = table.Column<string>(type: "text", nullable: false),
                    DistractorAudio = table.Column<string>(type: "text", nullable: false),
                    Ready = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_dialogues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "practice_progress",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Window = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_progress", x => new { x.UserId, x.Language });
                    table.ForeignKey(
                        name: "FK_practice_progress_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "practice_exposures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    DialogueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Swap = table.Column<bool>(type: "boolean", nullable: false),
                    FirstCorrect = table.Column<bool>(type: "boolean", nullable: true),
                    SeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_exposures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_practice_exposures_practice_dialogues_DialogueId",
                        column: x => x.DialogueId,
                        principalTable: "practice_dialogues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_practice_exposures_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "practice_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExposureId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Choice = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_practice_attempts_practice_exposures_ExposureId",
                        column: x => x.ExposureId,
                        principalTable: "practice_exposures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_practice_attempts_ExposureId",
                table: "practice_attempts",
                column: "ExposureId");

            migrationBuilder.CreateIndex(
                name: "IX_practice_dialogues_Language_Fingerprint",
                table: "practice_dialogues",
                columns: new[] { "Language", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_practice_dialogues_Locale_Level_Ready",
                table: "practice_dialogues",
                columns: new[] { "Locale", "Level", "Ready" });

            migrationBuilder.CreateIndex(
                name: "IX_practice_exposures_DialogueId",
                table: "practice_exposures",
                column: "DialogueId");

            migrationBuilder.CreateIndex(
                name: "IX_practice_exposures_UserId_DialogueId",
                table: "practice_exposures",
                columns: new[] { "UserId", "DialogueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_practice_exposures_UserId_RequestId",
                table: "practice_exposures",
                columns: new[] { "UserId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_practice_exposures_UserId_SeenAt",
                table: "practice_exposures",
                columns: new[] { "UserId", "SeenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "practice_attempts");

            migrationBuilder.DropTable(
                name: "practice_progress");

            migrationBuilder.DropTable(
                name: "practice_exposures");

            migrationBuilder.DropTable(
                name: "practice_dialogues");
        }
    }
}
