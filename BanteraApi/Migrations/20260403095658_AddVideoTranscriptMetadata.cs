using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoTranscriptMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TranscriptCuesJson",
                table: "user_videos",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "TranscriptLanguageCode",
                table: "user_videos",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "und");

            migrationBuilder.Sql(
                """
                UPDATE user_videos
                SET "TranscriptLanguageCode" = COALESCE(
                        NULLIF(lower(split_part(replace("TranscriptLanguage", '_', '-'), '-', 1)), ''),
                        'und'
                    ),
                    "TranscriptCuesJson" = '[]'::jsonb
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TranscriptCuesJson",
                table: "user_videos");

            migrationBuilder.DropColumn(
                name: "TranscriptLanguageCode",
                table: "user_videos");
        }
    }
}
