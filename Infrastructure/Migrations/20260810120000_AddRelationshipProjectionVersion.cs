using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(UserDbContext))]
[Migration("20260810120000_AddRelationshipProjectionVersion")]
public partial class AddRelationshipProjectionVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "T_RelationshipProjectionVersion",
            columns: table => new
            {
                OwnerUserId = table.Column<long>(type: "bigint", nullable: false),
                ListType = table.Column<short>(type: "smallint", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAtMs = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_T_RelationshipProjectionVersion",
                    x => new { x.OwnerUserId, x.ListType });
            });

        migrationBuilder.CreateIndex(
            name: "IX_RelationshipProjectionVersion_UpdatedAtMs",
            table: "T_RelationshipProjectionVersion",
            column: "UpdatedAtMs");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "T_RelationshipProjectionVersion");
    }
}
