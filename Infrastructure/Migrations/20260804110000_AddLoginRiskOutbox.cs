using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Moves post-login geo/ASN analysis from a lossy API process-local queue to a
/// durable, retryable Worker-role outbox.
/// </summary>
[DbContext(typeof(UserDbContext))]
[Migration("20260804110000_AddLoginRiskOutbox")]
public partial class AddLoginRiskOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "T_LoginRiskOutbox",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                ClientIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                DeviceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                IsNewDevice = table.Column<bool>(type: "boolean", nullable: false),
                IpChanged = table.Column<bool>(type: "boolean", nullable: false),
                SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Status = table.Column<byte>(type: "smallint", nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                LeaseToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_T_LoginRiskOutbox", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_LoginRiskOutbox_Status_NextAttemptAt",
            table: "T_LoginRiskOutbox",
            columns: new[] { "Status", "NextAttemptAt" });

        migrationBuilder.CreateIndex(
            name: "IX_LoginRiskOutbox_Status_LeaseExpiresAt",
            table: "T_LoginRiskOutbox",
            columns: new[] { "Status", "LeaseExpiresAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "T_LoginRiskOutbox");
}
