using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(UserDbContext))]
[Migration("20260731160000_AddSecurityVersion")]
public partial class AddSecurityVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "SecurityVersion",
            table: "AspNetUsers",
            type: "bigint",
            nullable: false,
            defaultValue: 1L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SecurityVersion",
            table: "AspNetUsers");
    }
}
