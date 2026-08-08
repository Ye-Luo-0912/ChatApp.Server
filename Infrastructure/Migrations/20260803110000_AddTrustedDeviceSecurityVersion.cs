using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Makes trusted-device credentials obey the same durable security fence as
/// access and refresh tokens. This is a safety net for delayed revocation
/// outbox work, not a replacement for the durable cleanup.
/// </summary>
public partial class AddTrustedDeviceSecurityVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "SecurityVersion",
            table: "T_TrustedDevice",
            type: "bigint",
            nullable: false,
            defaultValue: 1L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SecurityVersion",
            table: "T_TrustedDevice");
    }
}
