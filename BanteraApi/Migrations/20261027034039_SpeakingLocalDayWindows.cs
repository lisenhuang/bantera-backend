using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class SpeakingLocalDayWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "speaking_day_windows",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Language = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    FromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ThroughUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SpokenWords = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_speaking_day_windows", x => new { x.UserId, x.DeviceId, x.Date, x.Language });
                    table.ForeignKey(
                        name: "FK_speaking_day_windows_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_speaking_day_windows_ThroughUtc",
                table: "speaking_day_windows",
                column: "ThroughUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "speaking_day_windows");
        }
    }
}
