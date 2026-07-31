using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(UserDbContext))]
[Migration("20260731170000_AddAttachmentScanAudit")]
public partial class AddAttachmentScanAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "T_AttachmentScanAudit",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ScanJobId = table.Column<long>(type: "bigint", nullable: false),
                AttachmentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                EngineName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                EngineVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Verdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Allowed = table.Column<bool>(type: "boolean", nullable: false),
                IsTransient = table.Column<bool>(type: "boolean", nullable: false),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_T_AttachmentScanAudit", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AttachmentScanAudit_Attachment_Created",
            table: "T_AttachmentScanAudit",
            columns: new[] { "AttachmentId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_AttachmentScanAudit_Job_Created",
            table: "T_AttachmentScanAudit",
            columns: new[] { "ScanJobId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "T_AttachmentScanAudit");
    }
}
