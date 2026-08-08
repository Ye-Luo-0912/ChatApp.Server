using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CheckSecurityStateModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "T_BlockRecords");

            migrationBuilder.DropTable(
                name: "T_FriendRequests");

            migrationBuilder.DropTable(
                name: "T_UserFriendEntry");

            migrationBuilder.DropTable(
                name: "T_FriendGroup");

            migrationBuilder.DropColumn(
                name: "FriendRequestPolicy",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "NotifyFriendRequests",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<long>(
                name: "SecurityVersion",
                table: "T_TrustedDevice",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DownloadLeaseUntil",
                table: "T_DataExportJob",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "AccountState",
                table: "AspNetUsers",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<int>(
                name: "DeletionAttemptCount",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletionDeadLetterAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_DeletionDue",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SecurityVersion",
                table: "T_TrustedDevice");

            migrationBuilder.DropColumn(
                name: "DownloadLeaseUntil",
                table: "T_DataExportJob");

            migrationBuilder.DropColumn(
                name: "AccountState",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DeletionAttemptCount",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DeletionDeadLetterAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DeletionLastError",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DeletionNextAttemptAt",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<byte>(
                name: "FriendRequestPolicy",
                table: "AspNetUsers",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyFriendRequests",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "T_BlockRecords",
                columns: table => new
                {
                    BlockId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BlockedUserId = table.Column<long>(type: "bigint", nullable: false),
                    BlockerId = table.Column<long>(type: "bigint", nullable: false),
                    BlockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_BlockRecords", x => x.BlockId);
                    table.ForeignKey(
                        name: "FK_T_BlockRecords_AspNetUsers_BlockedUserId",
                        column: x => x.BlockedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_T_BlockRecords_AspNetUsers_BlockerId",
                        column: x => x.BlockerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "T_FriendGroup",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GroupName = table.Column<string>(type: "text", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_FriendGroup", x => x.GroupId);
                    table.ForeignKey(
                        name: "FK_T_FriendGroup_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "T_FriendRequests",
                columns: table => new
                {
                    RequestId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequesterId = table.Column<long>(type: "bigint", nullable: false),
                    TargetUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_FriendRequests", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_T_FriendRequests_AspNetUsers_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_T_FriendRequests_AspNetUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "T_UserFriendEntry",
                columns: table => new
                {
                    FriendshipId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendId = table.Column<long>(type: "bigint", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_UserFriendEntry", x => x.FriendshipId);
                    table.ForeignKey(
                        name: "FK_T_UserFriendEntry_AspNetUsers_FriendId",
                        column: x => x.FriendId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_T_UserFriendEntry_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_T_UserFriendEntry_T_FriendGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "T_FriendGroup",
                        principalColumn: "GroupId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_T_BlockRecords_BlockedUserId",
                table: "T_BlockRecords",
                column: "BlockedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_T_BlockRecords_BlockerId_BlockedUserId",
                table: "T_BlockRecords",
                columns: new[] { "BlockerId", "BlockedUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FriendGroup_UserId_SortOrder",
                table: "T_FriendGroup",
                columns: new[] { "UserId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_T_FriendGroup_UserId_GroupName",
                table: "T_FriendGroup",
                columns: new[] { "UserId", "GroupName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FriendRequest_TargetUser",
                table: "T_FriendRequests",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_T_FriendRequests_RequesterId_Status",
                table: "T_FriendRequests",
                columns: new[] { "RequesterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_T_FriendRequests_RequesterId_TargetUserId",
                table: "T_FriendRequests",
                columns: new[] { "RequesterId", "TargetUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_T_FriendRequests_TargetUserId_Status",
                table: "T_FriendRequests",
                columns: new[] { "TargetUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_T_UserFriendEntry_FriendId",
                table: "T_UserFriendEntry",
                column: "FriendId");

            migrationBuilder.CreateIndex(
                name: "IX_T_UserFriendEntry_GroupId",
                table: "T_UserFriendEntry",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_User_Friend_Unique",
                table: "T_UserFriendEntry",
                columns: new[] { "UserId", "FriendId" },
                unique: true);
        }
    }
}
