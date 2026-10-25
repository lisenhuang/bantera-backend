using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class PracticeLevelCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HighestPassedLevel",
                table: "practice_progress",
                type: "integer",
                nullable: false,
                defaultValue: -2);

            // Keep existing earned levels. Reaching C2 alone is not a pass,
            // but an already recorded qualifying C2 window is evidence of one.
            migrationBuilder.Sql("""
                UPDATE practice_progress
                SET "HighestPassedLevel" = CASE
                    WHEN "Level" = 5 AND length("Window") = 12
                         AND length(replace("Window", '0', '')) >= 10 THEN 5
                    ELSE GREATEST(-2, "Level" - 1)
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HighestPassedLevel",
                table: "practice_progress");
        }
    }
}
