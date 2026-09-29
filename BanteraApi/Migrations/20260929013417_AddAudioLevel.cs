using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Level",
                table: "user_videos",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Level",
                table: "user_videos");
        }
    }
}
