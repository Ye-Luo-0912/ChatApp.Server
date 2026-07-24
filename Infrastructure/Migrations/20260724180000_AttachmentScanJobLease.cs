using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(UserDbContext))]
    [Migration("20260724180000_AttachmentScanJobLease")]
    public partial class AttachmentScanJobLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "T_AttachmentScanJob",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "T_AttachmentScanJob",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentScanJob_ActiveAttachment",
                table: "T_AttachmentScanJob",
                column: "AttachmentId",
                unique: true,
                filter: "\"Status\" IN ('Pending', 'Processing', 'Finalizing')");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentScanJob_LeaseDue",
                table: "T_AttachmentScanJob",
                columns: new[] { "Status", "LeaseExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AttachmentScanJob_LeaseDue",
                table: "T_AttachmentScanJob");

            migrationBuilder.DropIndex(
                name: "IX_AttachmentScanJob_ActiveAttachment",
                table: "T_AttachmentScanJob");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "T_AttachmentScanJob");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "T_AttachmentScanJob");
        }
    }
}
