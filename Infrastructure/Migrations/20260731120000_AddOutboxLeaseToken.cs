using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(UserDbContext))]
    [Migration("20260731120000_AddOutboxLeaseToken")]
    public partial class AddOutboxLeaseToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // P0-4：为 Notification/Email Outbox 添加 LeaseToken fencing 列。
            // 替代 LockedAt 作为完成/失败/释放操作的匹配条件，避免 timestamptz 微秒精度
            // 与 .NET tick 精度不一致导致的 WHERE 匹配失败和重复发送。
            migrationBuilder.AddColumn<string>(
                name: "LeaseToken",
                table: "T_NotificationOutbox",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseToken",
                table: "T_EmailOutbox",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "LeaseToken", table: "T_EmailOutbox");
            migrationBuilder.DropColumn(name: "LeaseToken", table: "T_NotificationOutbox");
        }
    }
}
