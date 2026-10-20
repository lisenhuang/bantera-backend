using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class PracticeReviewLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SelectedLevel",
                table: "practice_progress",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReviewMode",
                table: "practice_exposures",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelectedLevel",
                table: "practice_progress");

            migrationBuilder.DropColumn(
                name: "ReviewMode",
                table: "practice_exposures");
        }
    }
}
