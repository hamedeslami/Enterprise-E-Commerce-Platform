using Ecommerce.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Modules.Identity.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261115103000_AddSessionRefreshPersistence")]
public sealed class AddSessionRefreshPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuthSessions",
            schema: "identity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                ActorKind = table.Column<int>(type: "integer", nullable: false),
                AuthenticationMethod = table.Column<int>(type: "integer", nullable: false),
                AuthenticatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                IdleExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RevocationReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                UserAgentHash = table.Column<byte[]>(type: "bytea", nullable: true),
                DeviceLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuthSessions", x => x.Id);
                table.CheckConstraint("CK_AuthSessions_ActorKind", "\"ActorKind\" IN (1, 2)");
                table.CheckConstraint("CK_AuthSessions_AuthenticationMethod", "\"AuthenticationMethod\" IN (1, 2, 3, 4)");
                table.CheckConstraint(
                    "CK_AuthSessions_Lifetime",
                    "\"CreatedAt\" < \"AbsoluteExpiresAt\" AND \"CreatedAt\" < \"IdleExpiresAt\" AND \"IdleExpiresAt\" <= \"AbsoluteExpiresAt\"");
                table.CheckConstraint(
                    "CK_AuthSessions_UserAgentHash",
                    "\"UserAgentHash\" IS NULL OR octet_length(\"UserAgentHash\") = 32");
                table.ForeignKey(
                    name: "FK_AuthSessions_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalSchema: "identity",
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RefreshTokens",
            schema: "identity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ReplacedByTokenId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                table.CheckConstraint("CK_RefreshTokens_HashLength", "octet_length(\"SecretHash\") = 32");
                table.CheckConstraint("CK_RefreshTokens_Lifetime", "\"CreatedAt\" < \"ExpiresAt\"");
                table.CheckConstraint(
                    "CK_RefreshTokens_Replacement",
                    "(\"ConsumedAt\" IS NULL AND \"ReplacedByTokenId\" IS NULL) OR (\"ConsumedAt\" IS NOT NULL AND \"ReplacedByTokenId\" IS NOT NULL)");
                table.CheckConstraint(
                    "CK_RefreshTokens_NotSelfReplacing",
                    "\"ReplacedByTokenId\" IS NULL OR \"ReplacedByTokenId\" <> \"Id\"");
                table.ForeignKey(
                    name: "FK_RefreshTokens_AuthSessions_SessionId",
                    column: x => x.SessionId,
                    principalSchema: "identity",
                    principalTable: "AuthSessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuthSessions_UserId_CreatedAt",
            schema: "identity",
            table: "AuthSessions",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_SecretHash",
            schema: "identity",
            table: "RefreshTokens",
            column: "SecretHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_SessionId",
            schema: "identity",
            table: "RefreshTokens",
            column: "SessionId");

        migrationBuilder.CreateIndex(
            name: "UX_RefreshTokens_OneActivePerSession",
            schema: "identity",
            table: "RefreshTokens",
            column: "SessionId",
            unique: true,
            filter: "\"ConsumedAt\" IS NULL AND \"RevokedAt\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RefreshTokens", schema: "identity");
        migrationBuilder.DropTable(name: "AuthSessions", schema: "identity");
    }
}
