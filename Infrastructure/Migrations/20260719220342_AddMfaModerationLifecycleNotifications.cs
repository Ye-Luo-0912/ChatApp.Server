using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMfaModerationLifecycleNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BanUntil",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletionScheduledAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyFriendRequests",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifySecurityEmail",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryCodesHashJson",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TotpSecret",
                table: "AspNetUsers",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "T_InAppNotification",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_InAppNotification", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "T_UserReport",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReporterId = table.Column<long>(type: "bigint", nullable: false),
                    TargetType = table.Column<byte>(type: "smallint", nullable: false),
                    TargetUserId = table.Column<long>(type: "bigint", nullable: true),
                    TargetMessageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<byte>(type: "smallint", nullable: false),
                    AppealNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BanUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedByAdminId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_UserReport", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotification_UserId_Id",
                table: "T_InAppNotification",
                columns: new[] { "UserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_UserReport_Status_Id",
                table: "T_UserReport",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_UserReport_TargetUser",
                table: "T_UserReport",
                column: "TargetUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "T_InAppNotification");

            migrationBuilder.DropTable(
                name: "T_UserReport");

            migrationBuilder.DropColumn(
                name: "BanUntil",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DeletionScheduledAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "NotifyFriendRequests",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "NotifySecurityEmail",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RecoveryCodesHashJson",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TotpSecret",
                table: "AspNetUsers");
        }
    }
}
