using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenDataExportUniquenessAndCleanupSaga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "T_AccountCleanupSaga",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_AccountCleanupSaga", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "T_AccountCleanupInbox",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_AccountCleanupInbox", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "T_AccountCleanupDeadLetter",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PayloadJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DeliveryCount = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_AccountCleanupDeadLetter", x => x.Id);
                });

            // 升级安全：先合并重复活跃导出，再创建唯一索引。
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "UserId"
                               ORDER BY "CreatedAt" DESC, "Id" DESC
                           ) AS rn
                    FROM "T_DataExportJob"
                    WHERE "ConsumedAt" IS NULL
                      AND "Status" IN ('Pending', 'Processing', 'Ready')
                )
                UPDATE "T_DataExportJob" AS j
                SET "Status" = 'Failed',
                    "Error" = 'superseded_duplicate_active',
                    "LeaseOwner" = NULL,
                    "LeaseUntil" = NULL
                FROM ranked AS r
                WHERE j."Id" = r."Id" AND r.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_DataExportJob_OneActive",
                table: "T_DataExportJob",
                column: "UserId",
                unique: true,
                filter: "\"ConsumedAt\" IS NULL AND \"Status\" IN ('Pending', 'Processing', 'Ready')");

            migrationBuilder.CreateIndex(
                name: "IX_AccountCleanupSaga_Status_Created",
                table: "T_AccountCleanupSaga",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountCleanupInbox_User_Processed",
                table: "T_AccountCleanupInbox",
                columns: new[] { "UserId", "ProcessedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_AccountCleanupDeadLetter_Event_Reason",
                table: "T_AccountCleanupDeadLetter",
                columns: new[] { "EventId", "ReasonCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountCleanupDeadLetter_Created",
                table: "T_AccountCleanupDeadLetter",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "T_AccountCleanupDeadLetter");

            migrationBuilder.DropTable(
                name: "T_AccountCleanupInbox");

            migrationBuilder.DropTable(
                name: "T_AccountCleanupSaga");

            migrationBuilder.DropIndex(
                name: "UX_DataExportJob_OneActive",
                table: "T_DataExportJob");
        }
    }
}
