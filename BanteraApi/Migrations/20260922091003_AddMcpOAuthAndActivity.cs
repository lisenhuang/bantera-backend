using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BanteraApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpOAuthAndActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mcp_audit_log",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Tool = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ArgsJson = table.Column<string>(type: "jsonb", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResultSummary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_audit_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "oauth_clients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ClientId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClientSecretHash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ClientName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RedirectUris = table.Column<string>(type: "jsonb", nullable: false),
                    GrantTypes = table.Column<string>(type: "jsonb", nullable: false),
                    TokenEndpointAuthMethod = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ClientUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsStatic = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_clients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_activity_daily",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TouchCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    MessagesSent = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "live")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_activity_daily", x => new { x.UserId, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "oauth_authorization_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OAuthClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    RedirectUri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Resource = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RequestedScopes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    GrantedScopes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CodeChallenge = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CodeChallengeMethod = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    State = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsentedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeniedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RedeemedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_authorization_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_oauth_authorization_codes_oauth_clients_OAuthClientId",
                        column: x => x.OAuthClientId,
                        principalTable: "oauth_clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oauth_authorization_codes_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TokenLookup = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OAuthClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorizationCodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Scopes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedByTokenId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_oauth_refresh_tokens_oauth_clients_OAuthClientId",
                        column: x => x.OAuthClientId,
                        principalTable: "oauth_clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oauth_refresh_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_audit_log_AdminUserId_CreatedAt",
                table: "mcp_audit_log",
                columns: new[] { "AdminUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_audit_log_CreatedAt",
                table: "mcp_audit_log",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_audit_log_TargetUserId",
                table: "mcp_audit_log",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_audit_log_Tool_CreatedAt",
                table: "mcp_audit_log",
                columns: new[] { "Tool", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_oauth_authorization_codes_CodeHash",
                table: "oauth_authorization_codes",
                column: "CodeHash",
                unique: true,
                filter: "\"CodeHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_authorization_codes_ExpiresAt",
                table: "oauth_authorization_codes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_authorization_codes_OAuthClientId",
                table: "oauth_authorization_codes",
                column: "OAuthClientId");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_authorization_codes_UserId",
                table: "oauth_authorization_codes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_clients_ClientId",
                table: "oauth_clients",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oauth_refresh_tokens_FamilyId",
                table: "oauth_refresh_tokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_refresh_tokens_OAuthClientId",
                table: "oauth_refresh_tokens",
                column: "OAuthClientId");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_refresh_tokens_TokenLookup",
                table: "oauth_refresh_tokens",
                column: "TokenLookup",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oauth_refresh_tokens_UserId",
                table: "oauth_refresh_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_activity_daily_Date",
                table: "user_activity_daily",
                column: "Date");

            // Indexes the analytics queries rely on. These tables are small, so a plain
            // (transactional) CREATE INDEX is fine; CONCURRENTLY cannot run inside the
            // migration transaction anyway.
            migrationBuilder.Sql(BanteraApi.Activity.ActivityBackfill.AnalyticsIndexesSql);

            // Seed approximate activity history from timestamps that already exist, so
            // DAU/WAU/MAU are not empty on day one. Idempotent.
            migrationBuilder.Sql(BanteraApi.Activity.ActivityBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_audit_log");

            migrationBuilder.DropTable(
                name: "oauth_authorization_codes");

            migrationBuilder.DropTable(
                name: "oauth_refresh_tokens");

            migrationBuilder.DropTable(
                name: "user_activity_daily");

            migrationBuilder.DropTable(
                name: "oauth_clients");

            migrationBuilder.Sql(BanteraApi.Activity.ActivityBackfill.DropAnalyticsIndexesSql);
        }
    }
}
