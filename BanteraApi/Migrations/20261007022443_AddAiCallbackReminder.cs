using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAiCallbackReminder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Reminder",
                table: "ai_callbacks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Reminder",
                table: "ai_callbacks");
        }
    }
}
