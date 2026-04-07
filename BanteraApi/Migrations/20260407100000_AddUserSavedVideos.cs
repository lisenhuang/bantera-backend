using System;
using BanteraApi.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260407100000_AddUserSavedVideos")]
    public partial class AddUserSavedVideos : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_saved_videos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoId = table.Column<Guid>(type: "uuid", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_saved_videos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_saved_videos_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_saved_videos_user_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "user_videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_saved_videos_VideoId",
                table: "user_saved_videos",
                column: "VideoId");

            migrationBuilder.CreateIndex(
                name: "IX_user_saved_videos_UserId_VideoId",
                table: "user_saved_videos",
                columns: new[] { "UserId", "VideoId" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_user_saved_videos_VideoId", table: "user_saved_videos");
            migrationBuilder.DropIndex(name: "IX_user_saved_videos_UserId_VideoId", table: "user_saved_videos");
            migrationBuilder.DropTable(name: "user_saved_videos");
        }
    }
}
