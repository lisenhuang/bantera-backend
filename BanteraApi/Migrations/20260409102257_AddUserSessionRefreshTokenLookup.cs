using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSessionRefreshTokenLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RefreshTokenLookup",
                table: "user_sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_sessions_RefreshTokenLookup",
                table: "user_sessions",
                column: "RefreshTokenLookup",
                unique: true,
                filter: "\"RefreshTokenLookup\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_user_sessions_RefreshTokenLookup",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "RefreshTokenLookup",
                table: "user_sessions");
        }
    }
}
