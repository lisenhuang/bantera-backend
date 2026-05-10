using BanteraApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260510000000_AddPushTokenCallCapability")]
    public partial class AddPushTokenCallCapability : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SupportsCalls",
                table: "user_push_tokens",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupportsCalls",
                table: "user_push_tokens");
        }
    }
}
