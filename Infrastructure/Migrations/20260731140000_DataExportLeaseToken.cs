using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(UserDbContext))]
    [Migration("20260731140000_DataExportLeaseToken")]
    public partial class DataExportLeaseToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // P0-5.2：为数据导出作业添加 LeaseToken fencing 列。
            // 完成失败续租操作匹配 Id+Status(Processing)+LeaseOwner+LeaseToken，避免租约过期被重新领取后旧持有者覆盖终态。
            migrationBuilder.AddColumn<string>(
                name: "LeaseToken",
                table: "T_DataExportJob",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "LeaseToken", table: "T_DataExportJob");
        }
    }
}
