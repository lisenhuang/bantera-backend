using System;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260414153000_AddBanteraAiOwnerForPreservedAudio")]
    public partial class AddBanteraAiOwnerForPreservedAudio : Migration
    {
        private static readonly Guid BanteraAiUserId = new("816cd28a-7629-4400-948b-4e0b65bd3638");
        private static readonly DateTime SeededAt = new(2026, 4, 14, 0, 0, 0, DateTimeKind.Utc);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "users",
                columns: new[] { "Id", "Name", "Role", "Status", "CreatedAt", "UpdatedAt", "DeletedAt" },
                columnTypes: new[] { "uuid", "character varying(80)", "character varying(20)", "character varying(50)", "timestamp with time zone", "timestamp with time zone", "timestamp with time zone" },
                values: new object[]
                {
                    BanteraAiUserId,
                    "Bantera AI",
                    "system",
                    "system",
                    SeededAt,
                    SeededAt,
                    SeededAt
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "users",
                keyColumn: "Id",
                keyColumnType: "uuid",
                keyValue: BanteraAiUserId);
        }
    }
}
