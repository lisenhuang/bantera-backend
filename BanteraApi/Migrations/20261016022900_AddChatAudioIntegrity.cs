using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddChatAudioIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AudioByteCount",
                table: "chat_messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioSha256",
                table: "chat_messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioByteCount",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "AudioSha256",
                table: "chat_messages");
        }
    }
}
