using Microsoft.EntityFrameworkCore.Migrations;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(UserDbContext))]
[Migration("20260802130000_DataExportNextAttemptAt")]
public partial class DataExportNextAttemptAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "NextAttemptAt",
            table: "T_DataExportJob",
            type: "timestamp with time zone",
            nullable: false,
            defaultValueSql: "CURRENT_TIMESTAMP");

        migrationBuilder.CreateIndex(
            name: "IX_DataExportJob_Due",
            table: "T_DataExportJob",
            columns: new[] { "Status", "NextAttemptAt", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_DataExportJob_Due",
            table: "T_DataExportJob");

        migrationBuilder.DropColumn(
            name: "NextAttemptAt",
            table: "T_DataExportJob");
    }
}
