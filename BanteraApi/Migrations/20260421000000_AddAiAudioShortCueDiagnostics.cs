using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    public partial class AddAiAudioShortCueDiagnostics : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_audio_short_cue_diagnostics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    VideoId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LanguageCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScenarioId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LongAlignmentMode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ShortAlignmentAttempted = table.Column<bool>(type: "boolean", nullable: false),
                    LineCount = table.Column<int>(type: "integer", nullable: false),
                    LinesWithSplitCount = table.Column<int>(type: "integer", nullable: false),
                    FlattenedShortCueCount = table.Column<int>(type: "integer", nullable: false),
                    LongCueCount = table.Column<int>(type: "integer", nullable: false),
                    WordTimingCount = table.Column<int>(type: "integer", nullable: false),
                    DetailJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_audio_short_cue_diagnostics", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_audio_short_cue_diagnostics_CreatedAt",
                table: "ai_audio_short_cue_diagnostics",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ai_audio_short_cue_diagnostics_LanguageCode_Reason",
                table: "ai_audio_short_cue_diagnostics",
                columns: new[] { "LanguageCode", "Reason" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_audio_short_cue_diagnostics_Reason",
                table: "ai_audio_short_cue_diagnostics",
                column: "Reason");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_audio_short_cue_diagnostics");
        }
    }
}
