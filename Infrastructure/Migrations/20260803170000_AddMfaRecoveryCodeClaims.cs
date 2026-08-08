using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddMfaRecoveryCodeClaims : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "T_MfaRecoveryCodeClaim",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                ClaimToken = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                CodeDigest = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                OriginalCodesJson = table.Column<string>(type: "text", nullable: false),
                RemainingCodesJson = table.Column<string>(type: "text", nullable: false),
                State = table.Column<short>(type: "smallint", nullable: false),
                ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_T_MfaRecoveryCodeClaim", x => x.Id);
                table.ForeignKey(
                    name: "FK_T_MfaRecoveryCodeClaim_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "UX_MfaRecoveryCodeClaim_Token",
            table: "T_MfaRecoveryCodeClaim",
            column: "ClaimToken",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MfaRecoveryCodeClaim_State_Expires",
            table: "T_MfaRecoveryCodeClaim",
            columns: new[] { "State", "ExpiresAt" });

        migrationBuilder.CreateIndex(
            name: "IX_MfaRecoveryCodeClaim_User_State",
            table: "T_MfaRecoveryCodeClaim",
            columns: new[] { "UserId", "State" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "T_MfaRecoveryCodeClaim");
}
