using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(UserDbContext))]
[Migration("20260731180000_AddAttachmentScanProjection")]
public partial class AddAttachmentScanProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "T_AttachmentScanProjection",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ScanJobId = table.Column<long>(type: "bigint", nullable: false),
                AttachmentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                OriginalName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                LeaseToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_T_AttachmentScanProjection", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AttachmentScanProjection_Due",
            table: "T_AttachmentScanProjection",
            columns: new[] { "Status", "NextAttemptAt" });

        migrationBuilder.CreateIndex(
            name: "IX_AttachmentScanProjection_LeaseDue",
            table: "T_AttachmentScanProjection",
            columns: new[] { "Status", "LeaseExpiresAt" });

        migrationBuilder.CreateIndex(
            name: "IX_AttachmentScanProjection_ScanJob",
            table: "T_AttachmentScanProjection",
            column: "ScanJobId");

        migrationBuilder.CreateIndex(
            name: "UX_AttachmentScanProjection_ActiveScanJob",
            table: "T_AttachmentScanProjection",
            column: "ScanJobId",
            unique: true,
            filter: "\"Status\" IN ('Pending', 'Processing')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "T_AttachmentScanProjection");
    }
}
