using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileSecurityAdminFriendEnhancements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "T_FriendGroup",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "T_FriendGroup",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte>(
                name: "FriendRequestPolicy",
                table: "AspNetUsers",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UserNameChangedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "T_AdminAuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdminUserId = table.Column<long>(type: "bigint", nullable: false),
                    TargetUserId = table.Column<long>(type: "bigint", nullable: true),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Detail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ClientIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_AdminAuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "T_SecurityEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    EventType = table.Column<short>(type: "smallint", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ClientIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Location = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Detail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ActorUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_SecurityEvent", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FriendGroup_UserId_SortOrder",
                table: "T_FriendGroup",
                columns: new[] { "UserId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditLog_Admin_Created",
                table: "T_AdminAuditLog",
                columns: new[] { "AdminUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditLog_Target",
                table: "T_AdminAuditLog",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvent_UserId_Id",
                table: "T_SecurityEvent",
                columns: new[] { "UserId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "T_AdminAuditLog");

            migrationBuilder.DropTable(
                name: "T_SecurityEvent");

            migrationBuilder.DropIndex(
                name: "IX_FriendGroup_UserId_SortOrder",
                table: "T_FriendGroup");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "T_FriendGroup");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "T_FriendGroup");

            migrationBuilder.DropColumn(
                name: "FriendRequestPolicy",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "UserNameChangedAt",
                table: "AspNetUsers");
        }
    }
}
