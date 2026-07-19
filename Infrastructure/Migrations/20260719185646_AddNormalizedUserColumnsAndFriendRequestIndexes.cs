using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNormalizedUserColumnsAndFriendRequestIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "UserNameIndex",
                table: "AspNetUsers");

            migrationBuilder.EnsureSchema(
                name: "realtime");

            migrationBuilder.AlterColumn<string>(
                name: "Signature",
                table: "AspNetUsers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Region",
                table: "AspNetUsers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AvatarUrl",
                table: "AspNetUsers",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "AspNetUsers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedUserName",
                table: "AspNetUsers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "AspNetUsers"
                SET "NormalizedEmail" = UPPER("Email"),
                    "NormalizedUserName" = UPPER("UserName");
                """);

            // Optional future optimization for friend search (FriendshipService uses ILike on UserName/Note):
            // CREATE EXTENSION IF NOT EXISTS pg_trgm;
            // CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_AspNetUsers_UserName_trgm"
            //   ON "AspNetUsers" USING gin ("UserName" gin_trgm_ops);
            // CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_T_UserFriendEntry_Note_trgm"
            //   ON "T_UserFriendEntry" USING gin ("Note" gin_trgm_ops);

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "realtime",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    created_at_ms = table.Column<long>(type: "bigint", nullable: false),
                    next_attempt_at_ms = table.Column<long>(type: "bigint", nullable: false),
                    published_at_ms = table.Column<long>(type: "bigint", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    locked_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    locked_until_ms = table.Column<long>(type: "bigint", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_T_FriendRequests_RequesterId_Status",
                table: "T_FriendRequests",
                columns: new[] { "RequesterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_T_FriendRequests_TargetUserId_Status",
                table: "T_FriendRequests",
                columns: new[] { "TargetUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_published_at_ms_next_attempt_at_ms",
                schema: "realtime",
                table: "outbox",
                columns: new[] { "published_at_ms", "next_attempt_at_ms" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox",
                schema: "realtime");

            migrationBuilder.DropIndex(
                name: "IX_T_FriendRequests_RequesterId_Status",
                table: "T_FriendRequests");

            migrationBuilder.DropIndex(
                name: "IX_T_FriendRequests_TargetUserId_Status",
                table: "T_FriendRequests");

            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "UserNameIndex",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "NormalizedUserName",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<string>(
                name: "Signature",
                table: "AspNetUsers",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Region",
                table: "AspNetUsers",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AvatarUrl",
                table: "AspNetUsers",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "UserName",
                unique: true);
        }
    }
}
