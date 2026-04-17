using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    [Migration("20260417191500_AddTranscriptShortCuesJsonToUserVideo")]
    public partial class AddTranscriptShortCuesJsonToUserVideo : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TranscriptShortCuesJson",
                table: "user_videos",
                type: "jsonb",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TranscriptShortCuesJson",
                table: "user_videos");
        }
    }
}
