using Npgsql;

namespace ChatApp.Server.IntegrationTests.Support;

internal static class RealtimeAttachmentTestSchema
{
    public static async Task EnsureAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            CREATE SCHEMA IF NOT EXISTS realtime;
            CREATE TABLE IF NOT EXISTS realtime.attachments (
                attachment_id         varchar(64)   PRIMARY KEY,
                uploader_user_id      bigint        NOT NULL,
                object_key            varchar(512)  NOT NULL,
                public_url            varchar(1024) NULL,
                content_type          varchar(128)  NOT NULL,
                size_bytes            bigint        NOT NULL,
                original_name         varchar(256)  NULL,
                content_hash          varchar(64)   NULL,
                status                smallint      NOT NULL,
                message_id            varchar(64)   NULL,
                conversation_id       varchar(64)   NULL,
                client_attachment_id  varchar(128)  NULL,
                created_at_ms         bigint        NOT NULL,
                confirmed_at_ms       bigint        NULL,
                bound_at_ms           bigint        NULL
            );
            ALTER TABLE realtime.attachments
                ADD COLUMN IF NOT EXISTS content_hash varchar(64) NULL;
            ALTER TABLE realtime.attachments
                ADD COLUMN IF NOT EXISTS scan_projection_id bigint NULL;
            ALTER TABLE realtime.attachments
                ADD COLUMN IF NOT EXISTS scan_version bigint NOT NULL DEFAULT 0;
            ALTER TABLE realtime.attachments
                DROP CONSTRAINT IF EXISTS ck_attachments_status_known;
            ALTER TABLE realtime.attachments
                ADD CONSTRAINT ck_attachments_status_known
                CHECK (status IN (0, 1, 2, 3, 4, 5, 6));
            CREATE UNIQUE INDEX IF NOT EXISTS ux_attachments_object_key
                ON realtime.attachments (object_key);
            CREATE TABLE IF NOT EXISTS realtime.conversation_members (
                conversation_id varchar(64) NOT NULL,
                user_id         bigint      NOT NULL,
                joined_at_ms    bigint      NOT NULL,
                PRIMARY KEY (conversation_id, user_id)
            );
            ALTER TABLE realtime.conversation_members
                ADD COLUMN IF NOT EXISTS joined_at_ms bigint NOT NULL DEFAULT 0;
            CREATE TABLE IF NOT EXISTS realtime.messages (
                message_id       varchar(64) PRIMARY KEY,
                sender_user_id   bigint      NOT NULL,
                receiver_user_id bigint      NOT NULL,
                conversation_id  varchar(64) NULL,
                content          text        NULL,
                created_at_ms    bigint      NOT NULL DEFAULT 0
            );
            """,
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
