using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    [Migration("20260417000000_AddSavedCueSegmentMetadata")]
    public partial class AddSavedCueSegmentMetadata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CueText",
                table: "user_saved_cues",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StartTimeMs",
                table: "user_saved_cues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EndTimeMs",
                table: "user_saved_cues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CueMode",
                table: "user_saved_cues",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentCueId",
                table: "user_saved_cues",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParentCueIndex",
                table: "user_saved_cues",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CueText",
                table: "user_saved_cues");

            migrationBuilder.DropColumn(
                name: "StartTimeMs",
                table: "user_saved_cues");

            migrationBuilder.DropColumn(
                name: "EndTimeMs",
                table: "user_saved_cues");

            migrationBuilder.DropColumn(
                name: "CueMode",
                table: "user_saved_cues");

            migrationBuilder.DropColumn(
                name: "ParentCueId",
                table: "user_saved_cues");

            migrationBuilder.DropColumn(
                name: "ParentCueIndex",
                table: "user_saved_cues");
        }
    }
}
