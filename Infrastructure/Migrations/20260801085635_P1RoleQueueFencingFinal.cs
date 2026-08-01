using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class P1RoleQueueFencingFinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AttachmentBlobDeleteJob_ObjectKey",
                table: "T_AttachmentBlobDeleteJob");

            migrationBuilder.AlterColumn<string>(
                name: "Note",
                table: "T_UserFriendEntry",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Message",
                table: "T_FriendRequests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "T_AttachmentBlobDeleteJob",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "T_AttachmentBlobDeleteJob",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseToken",
                table: "T_AttachmentBlobDeleteJob",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "ObjectKey"
                               ORDER BY CASE WHEN "Status" = 'Processing' THEN 0 ELSE 1 END,
                                        "LeaseExpiresAt" DESC NULLS LAST,
                                        "CreatedAt",
                                        "Id") AS "Rank"
                    FROM "T_AttachmentBlobDeleteJob"
                    WHERE "Status" IN ('Pending', 'Processing')
                )
                UPDATE "T_AttachmentBlobDeleteJob" AS job
                SET "Status" = 'DeadLetter',
                    "CompletedAt" = CURRENT_TIMESTAMP,
                    "LastError" = 'superseded duplicate active tombstone during migration',
                    "NextAttemptAt" = CURRENT_TIMESTAMP,
                    "LeaseOwner" = NULL,
                    "LeaseToken" = NULL,
                    "LeaseExpiresAt" = NULL
                FROM ranked
                WHERE job."Id" = ranked."Id"
                  AND ranked."Rank" > 1;
                """);

            migrationBuilder.CreateTable(
                name: "T_ModerationSessionRevocationOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceReportId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedSecurityVersion = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedBanUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_ModerationSessionRevocationOutbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentBlobDeleteJob_Status_LeaseExpiresAt",
                table: "T_AttachmentBlobDeleteJob",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "UX_AttachmentBlobDeleteJob_ActiveObjectKey",
                table: "T_AttachmentBlobDeleteJob",
                column: "ObjectKey",
                unique: true,
                filter: "\"Status\" IN ('Pending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationSessionRevocationOutbox_Status_LeaseExpiresAt",
                table: "T_ModerationSessionRevocationOutbox",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationSessionRevocationOutbox_Status_NextAttemptAt",
                table: "T_ModerationSessionRevocationOutbox",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "UX_ModerationSessionRevocationOutbox_SourceReportId",
                table: "T_ModerationSessionRevocationOutbox",
                column: "SourceReportId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "T_ModerationSessionRevocationOutbox");

            migrationBuilder.DropIndex(
                name: "IX_AttachmentBlobDeleteJob_Status_LeaseExpiresAt",
                table: "T_AttachmentBlobDeleteJob");

            migrationBuilder.DropIndex(
                name: "UX_AttachmentBlobDeleteJob_ActiveObjectKey",
                table: "T_AttachmentBlobDeleteJob");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "T_AttachmentBlobDeleteJob");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "T_AttachmentBlobDeleteJob");

            migrationBuilder.DropColumn(
                name: "LeaseToken",
                table: "T_AttachmentBlobDeleteJob");

            migrationBuilder.AlterColumn<string>(
                name: "Note",
                table: "T_UserFriendEntry",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Message",
                table: "T_FriendRequests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentBlobDeleteJob_ObjectKey",
                table: "T_AttachmentBlobDeleteJob",
                column: "ObjectKey");
        }
    }
}
