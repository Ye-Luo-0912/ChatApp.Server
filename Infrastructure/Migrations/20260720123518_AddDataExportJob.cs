using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDataExportJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "T_DataExportJob",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadyAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_DataExportJob", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataExportJob_Claim",
                table: "T_DataExportJob",
                columns: new[] { "Status", "LeaseUntil", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DataExportJob_User_Status",
                table: "T_DataExportJob",
                columns: new[] { "UserId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "T_DataExportJob");
        }
    }
}
