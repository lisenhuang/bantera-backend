using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddWebsiteAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "website_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Path = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    LandingPath = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Source = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Evidence = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ReferrerHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Campaign = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Medium = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Language = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Device = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_website_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_website_events_ReceivedAt",
                table: "website_events",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_website_events_SessionId_ReceivedAt",
                table: "website_events",
                columns: new[] { "SessionId", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "website_events");
        }
    }
}
