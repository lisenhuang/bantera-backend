using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAiCallbacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_callbacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PushTokenId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_callbacks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_callbacks_user_push_tokens_PushTokenId",
                        column: x => x.PushTokenId,
                        principalTable: "user_push_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ai_callbacks_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_callbacks_PushTokenId",
                table: "ai_callbacks",
                column: "PushTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_callbacks_Status_DueAt",
                table: "ai_callbacks",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_callbacks_UserId_RequestKey",
                table: "ai_callbacks",
                columns: new[] { "UserId", "RequestKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_callbacks");
        }
    }
}
