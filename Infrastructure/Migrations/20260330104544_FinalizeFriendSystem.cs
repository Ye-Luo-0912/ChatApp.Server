    using Microsoft.EntityFrameworkCore.Migrations;

    #nullable disable

    namespace Infrastructure.Migrations
    {
        /// <inheritdoc />
        public partial class FinalizeFriendSystem : Migration
        {
            /// <inheritdoc />
            protected override void Up(MigrationBuilder migrationBuilder)
            {
                migrationBuilder.DropColumn(
                    name: "Status",
                    table: "T_Friendships");

                migrationBuilder.RenameColumn(
                    name: "AcceptedAt",
                    table: "T_Friendships",
                    newName: "DeletedAt");

                migrationBuilder.RenameIndex(
                    name: "IX_T_FriendRequests_TargetUserId",
                    table: "T_FriendRequests",
                    newName: "IX_FriendRequest_TargetUser");

                migrationBuilder.AlterColumn<int>(
                    name: "GroupId",
                    table: "T_Friendships",
                    type: "integer",
                    nullable: true,
                    oldClrType: typeof(int),
                    oldType: "integer");

                migrationBuilder.AlterColumn<long>(
                    name: "UserId",
                    table: "T_FriendGroup",
                    type: "bigint",
                    nullable: false,
                    defaultValue: 0L,
                    oldClrType: typeof(long),
                    oldType: "bigint",
                    oldNullable: true);
            }

            /// <inheritdoc />
            protected override void Down(MigrationBuilder migrationBuilder)
            {
                migrationBuilder.RenameColumn(
                    name: "DeletedAt",
                    table: "T_Friendships",
                    newName: "AcceptedAt");

                migrationBuilder.RenameIndex(
                    name: "IX_FriendRequest_TargetUser",
                    table: "T_FriendRequests",
                    newName: "IX_T_FriendRequests_TargetUserId");

                migrationBuilder.AlterColumn<int>(
                    name: "GroupId",
                    table: "T_Friendships",
                    type: "integer",
                    nullable: false,
                    defaultValue: 0,
                    oldClrType: typeof(int),
                    oldType: "integer",
                    oldNullable: true);

                migrationBuilder.AddColumn<byte>(
                    name: "Status",
                    table: "T_Friendships",
                    type: "smallint",
                    nullable: false,
                    defaultValue: (byte)0);

                migrationBuilder.AlterColumn<long>(
                    name: "UserId",
                    table: "T_FriendGroup",
                    type: "bigint",
                    nullable: true,
                    oldClrType: typeof(long),
                    oldType: "bigint");
            }
        }
    }
