using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Keeps a cancellation-in-flight export inside the per-user active-job
/// invariant until the worker durably reaches Cancelled.
/// </summary>
[Migration("20260804100000_IncludeCancelRequestedInDataExportActive")]
[DbContext(typeof(UserDbContext))]
public partial class IncludeCancelRequestedInDataExportActive : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_DataExportJob_OneActive",
            table: "T_DataExportJob");

        migrationBuilder.CreateIndex(
            name: "UX_DataExportJob_OneActive",
            table: "T_DataExportJob",
            column: "UserId",
            unique: true,
            filter: "\"ConsumedAt\" IS NULL AND \"Status\" IN ('Pending', 'Processing', 'CancelRequested', 'Ready')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_DataExportJob_OneActive",
            table: "T_DataExportJob");

        migrationBuilder.CreateIndex(
            name: "UX_DataExportJob_OneActive",
            table: "T_DataExportJob",
            column: "UserId",
            unique: true,
            filter: "\"ConsumedAt\" IS NULL AND \"Status\" IN ('Pending', 'Processing', 'Ready')");
    }
}
