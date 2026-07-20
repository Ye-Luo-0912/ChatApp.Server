using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenMfaNotificationOutboxDeletionLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Reason",
                table: "T_UserReport",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceSnapshot",
                table: "T_UserReport",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TotpSecret",
                table: "AspNetUsers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionLeaseOwner",
                table: "AspNetUsers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletionLeaseUntil",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingRecoveryCodesHashJson",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingTotpSecret",
                table: "AspNetUsers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "T_NotificationOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PreferEmail = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<byte>(type: "smallint", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LockedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockOwner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_NotificationOutbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserReport_Reporter_Target_Created",
                table: "T_UserReport",
                columns: new[] { "ReporterId", "TargetUserId", "TargetMessageId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_DeletionScheduledAt",
                table: "AspNetUsers",
                column: "DeletionScheduledAt",
                filter: "\"DeletionScheduledAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_IdempotencyKey_Active",
                table: "T_NotificationOutbox",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL AND \"Status\" IN (0, 1, 3)");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Status_LockedAt",
                table: "T_NotificationOutbox",
                columns: new[] { "Status", "LockedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Status_NextAttemptAt",
                table: "T_NotificationOutbox",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "T_NotificationOutbox");

            migrationBuilder.DropIndex(
                name: "IX_UserReport_Reporter_Target_Created",
                table: "T_UserReport");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_DeletionScheduledAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EvidenceSnapshot",
                table: "T_UserReport");

            migrationBuilder.DropColumn(
                name: "DeletionLeaseOwner",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DeletionLeaseUntil",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PendingRecoveryCodesHashJson",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PendingTotpSecret",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<string>(
                name: "Reason",
                table: "T_UserReport",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "TotpSecret",
                table: "AspNetUsers",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512,
                oldNullable: true);
        }
    }
}
