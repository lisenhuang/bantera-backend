using BanteraApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BanteraApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260927000000_RetainGroupChatMessages")]
public sealed class RetainGroupChatMessages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Retain every existing group message, including ones awaiting cleanup.
        migrationBuilder.Sql("""
            UPDATE chat_messages AS m SET "ExpiresAt" = NULL
            FROM chat_threads AS t
            WHERE m."ThreadId" = t."Id" AND t."Type" = 'group';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not schedule deletion of retained messages during a rollback.
    }
}
