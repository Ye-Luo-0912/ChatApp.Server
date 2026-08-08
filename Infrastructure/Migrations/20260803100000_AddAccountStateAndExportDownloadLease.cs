using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Adds the durable account lifecycle state and the download lease used by
/// export PII tombstones. Existing scheduled deletions are imported as the
/// restricted DeletionPending state before the new model is used by the API.
/// </summary>
public partial class AddAccountStateAndExportDownloadLease : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<short>(
            name: "AccountState",
            table: "AspNetUsers",
            type: "smallint",
            nullable: false,
            defaultValue: (short)0);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DownloadLeaseUntil",
            table: "T_DataExportJob",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.Sql(
            "UPDATE \"AspNetUsers\" SET \"AccountState\" = 1 " +
            "WHERE \"DeletionScheduledAt\" IS NOT NULL AND \"AccountState\" = 0;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DownloadLeaseUntil",
            table: "T_DataExportJob");

        migrationBuilder.DropColumn(
            name: "AccountState",
            table: "AspNetUsers");
    }
}
