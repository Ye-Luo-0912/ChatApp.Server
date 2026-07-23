using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRealtimeOutboxLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Shared realtime.outbox with RealtimeServices Migration003 - IF NOT EXISTS / backfill so either side can apply first.
            // status: 0 Pending, 1 Published, 2 Dead.
            migrationBuilder.Sql("""
                ALTER TABLE realtime.outbox ADD COLUMN IF NOT EXISTS "status" smallint NULL;

                UPDATE realtime.outbox
                SET "status" = CASE
                    WHEN "published_at_ms" IS NOT NULL THEN 1
                    ELSE 0
                END
                WHERE "status" IS NULL;

                ALTER TABLE realtime.outbox ALTER COLUMN "status" SET DEFAULT 0;
                ALTER TABLE realtime.outbox ALTER COLUMN "status" SET NOT NULL;

                DROP INDEX IF EXISTS realtime."ix_outbox_pending";
                CREATE INDEX IF NOT EXISTS "ix_outbox_pending"
                    ON realtime.outbox ("next_attempt_at_ms", "created_at_ms")
                    WHERE "status" = 0;
                CREATE INDEX IF NOT EXISTS "ix_outbox_dead"
                    ON realtime.outbox ("created_at_ms")
                    WHERE "status" = 2;
                CREATE INDEX IF NOT EXISTS "ix_outbox_published_cleanup"
                    ON realtime.outbox ("published_at_ms")
                    WHERE "status" = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS realtime."ix_outbox_published_cleanup";
                DROP INDEX IF EXISTS realtime."ix_outbox_dead";
                DROP INDEX IF EXISTS realtime."ix_outbox_pending";
                ALTER TABLE realtime.outbox DROP COLUMN IF EXISTS "status";
                """);
        }
    }
}
