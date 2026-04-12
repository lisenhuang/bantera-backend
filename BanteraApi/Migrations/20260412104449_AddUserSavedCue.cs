using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSavedCue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_saved_cues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoId = table.Column<Guid>(type: "uuid", nullable: false),
                    CueId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CueIndex = table.Column<int>(type: "integer", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_saved_cues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_saved_cues_user_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "user_videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_saved_cues_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_saved_cues_UserId_VideoId_CueId",
                table: "user_saved_cues",
                columns: new[] { "UserId", "VideoId", "CueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_saved_cues_VideoId",
                table: "user_saved_cues",
                column: "VideoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_saved_cues");
        }
    }
}
