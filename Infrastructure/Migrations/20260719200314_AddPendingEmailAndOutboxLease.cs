using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingEmailAndOutboxLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmailType",
                table: "T_EmailOutbox",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "T_EmailOutbox",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockOwner",
                table: "T_EmailOutbox",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedAt",
                table: "T_EmailOutbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedPendingEmail",
                table: "AspNetUsers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingEmail",
                table: "AspNetUsers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PendingEmailRequestedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_IdempotencyKey_Active",
                table: "T_EmailOutbox",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL AND \"Status\" IN (0, 1, 3)");

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_Status_LockedAt",
                table: "T_EmailOutbox",
                columns: new[] { "Status", "LockedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_NormalizedPendingEmail",
                table: "AspNetUsers",
                column: "NormalizedPendingEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailOutbox_IdempotencyKey_Active",
                table: "T_EmailOutbox");

            migrationBuilder.DropIndex(
                name: "IX_EmailOutbox_Status_LockedAt",
                table: "T_EmailOutbox");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_NormalizedPendingEmail",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailType",
                table: "T_EmailOutbox");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "T_EmailOutbox");

            migrationBuilder.DropColumn(
                name: "LockOwner",
                table: "T_EmailOutbox");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                table: "T_EmailOutbox");

            migrationBuilder.DropColumn(
                name: "NormalizedPendingEmail",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PendingEmail",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PendingEmailRequestedAt",
                table: "AspNetUsers");
        }
    }
}
