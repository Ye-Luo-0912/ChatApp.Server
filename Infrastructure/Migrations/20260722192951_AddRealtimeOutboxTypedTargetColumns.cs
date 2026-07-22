using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRealtimeOutboxTypedTargetColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Shared realtime.outbox with RealtimeServices Migration002 - IF NOT EXISTS / backfill so either side can apply first.
            migrationBuilder.Sql("""
                ALTER TABLE realtime.outbox ADD COLUMN IF NOT EXISTS "target_user_id" bigint NULL;
                ALTER TABLE realtime.outbox ADD COLUMN IF NOT EXISTS "event_type" smallint NULL;

                UPDATE realtime.outbox
                SET
                    "target_user_id" = COALESCE(
                        "target_user_id",
                        NULLIF(BTRIM("payload_json"::jsonb ->> 'TargetUserId'), '')::bigint),
                    "event_type" = COALESCE(
                        "event_type",
                        NULLIF(BTRIM("payload_json"::jsonb ->> 'Type'), '')::smallint)
                WHERE "target_user_id" IS NULL OR "event_type" IS NULL;

                UPDATE realtime.outbox
                SET
                    "target_user_id" = COALESCE("target_user_id", 0),
                    "event_type" = COALESCE("event_type", 0)
                WHERE "target_user_id" IS NULL OR "event_type" IS NULL;

                ALTER TABLE realtime.outbox ALTER COLUMN "target_user_id" SET DEFAULT 0;
                ALTER TABLE realtime.outbox ALTER COLUMN "event_type" SET DEFAULT 0;
                ALTER TABLE realtime.outbox ALTER COLUMN "target_user_id" SET NOT NULL;
                ALTER TABLE realtime.outbox ALTER COLUMN "event_type" SET NOT NULL;

                CREATE INDEX IF NOT EXISTS "ix_outbox_target_user_id" ON realtime.outbox ("target_user_id");
                CREATE INDEX IF NOT EXISTS "ix_outbox_target_user_event_type" ON realtime.outbox ("target_user_id", "event_type");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS realtime."ix_outbox_target_user_event_type";
                DROP INDEX IF EXISTS realtime."ix_outbox_target_user_id";
                ALTER TABLE realtime.outbox DROP COLUMN IF EXISTS "event_type";
                ALTER TABLE realtime.outbox DROP COLUMN IF EXISTS "target_user_id";
                """);
        }
    }
}
