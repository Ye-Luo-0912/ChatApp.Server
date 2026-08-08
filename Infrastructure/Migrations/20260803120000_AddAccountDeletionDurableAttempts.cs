using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Persists account-deletion retry/backoff and dead-letter state on the
/// durable user-row queue. A process crash can no longer reset the attempt
/// count or make a permanently failing account hot-loop forever.
/// </summary>
public partial class AddAccountDeletionDurableAttempts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletionDeadLetterAt",
            table: "AspNetUsers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "DeletionAttemptCount",
            table: "AspNetUsers",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "DeletionLastError",
            table: "AspNetUsers",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletionNextAttemptAt",
            table: "AspNetUsers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUsers_DeletionDue",
            table: "AspNetUsers",
            columns: new[] { "DeletionScheduledAt", "DeletionNextAttemptAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AspNetUsers_DeletionDue",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(name: "DeletionDeadLetterAt", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "DeletionAttemptCount", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "DeletionLastError", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "DeletionNextAttemptAt", table: "AspNetUsers");
    }
}
