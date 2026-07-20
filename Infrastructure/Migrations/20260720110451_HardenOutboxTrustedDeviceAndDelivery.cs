using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenOutboxTrustedDeviceAndDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TrustedDevice_User_Device",
                table: "T_TrustedDevice");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "T_TrustedDevice");

            migrationBuilder.AddColumn<string>(
                name: "DeviceIdHint",
                table: "T_TrustedDevice",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "T_TrustedDevice",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAt",
                table: "T_TrustedDevice",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "T_TrustedDevice",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailDeliveredAt",
                table: "T_NotificationOutbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InAppDeliveredAt",
                table: "T_NotificationOutbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceOutboxId",
                table: "T_InAppNotification",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevice_TokenHash",
                table: "T_TrustedDevice",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevice_User_Expires",
                table: "T_TrustedDevice",
                columns: new[] { "UserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotification_SourceOutboxId",
                table: "T_InAppNotification",
                column: "SourceOutboxId",
                unique: true,
                filter: "\"SourceOutboxId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotification_UserId_Unread",
                table: "T_InAppNotification",
                column: "UserId",
                filter: "\"IsRead\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TrustedDevice_TokenHash",
                table: "T_TrustedDevice");

            migrationBuilder.DropIndex(
                name: "IX_TrustedDevice_User_Expires",
                table: "T_TrustedDevice");

            migrationBuilder.DropIndex(
                name: "IX_InAppNotification_SourceOutboxId",
                table: "T_InAppNotification");

            migrationBuilder.DropIndex(
                name: "IX_InAppNotification_UserId_Unread",
                table: "T_InAppNotification");

            migrationBuilder.DropColumn(
                name: "DeviceIdHint",
                table: "T_TrustedDevice");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "T_TrustedDevice");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "T_TrustedDevice");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "T_TrustedDevice");

            migrationBuilder.DropColumn(
                name: "EmailDeliveredAt",
                table: "T_NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "InAppDeliveredAt",
                table: "T_NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "SourceOutboxId",
                table: "T_InAppNotification");

            migrationBuilder.AddColumn<string>(
                name: "DeviceId",
                table: "T_TrustedDevice",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevice_User_Device",
                table: "T_TrustedDevice",
                columns: new[] { "UserId", "DeviceId" },
                unique: true);
        }
    }
}
