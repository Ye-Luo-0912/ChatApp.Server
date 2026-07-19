using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameFriendshipTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_T_Friendships_AspNetUsers_FriendId",
                table: "T_Friendships");

            migrationBuilder.DropForeignKey(
                name: "FK_T_Friendships_AspNetUsers_UserId",
                table: "T_Friendships");

            migrationBuilder.DropForeignKey(
                name: "FK_T_Friendships_T_FriendGroup_GroupId",
                table: "T_Friendships");

            migrationBuilder.DropPrimaryKey(
                name: "PK_T_Friendships",
                table: "T_Friendships");

            migrationBuilder.RenameTable(
                name: "T_Friendships",
                newName: "T_UserFriendEntry");

            migrationBuilder.RenameIndex(
                name: "IX_T_Friendships_GroupId",
                table: "T_UserFriendEntry",
                newName: "IX_T_UserFriendEntry_GroupId");

            migrationBuilder.RenameIndex(
                name: "IX_T_Friendships_FriendId",
                table: "T_UserFriendEntry",
                newName: "IX_T_UserFriendEntry_FriendId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_T_UserFriendEntry",
                table: "T_UserFriendEntry",
                column: "FriendshipId");

            migrationBuilder.AddForeignKey(
                name: "FK_T_UserFriendEntry_AspNetUsers_FriendId",
                table: "T_UserFriendEntry",
                column: "FriendId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_T_UserFriendEntry_AspNetUsers_UserId",
                table: "T_UserFriendEntry",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_T_UserFriendEntry_T_FriendGroup_GroupId",
                table: "T_UserFriendEntry",
                column: "GroupId",
                principalTable: "T_FriendGroup",
                principalColumn: "GroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_T_UserFriendEntry_AspNetUsers_FriendId",
                table: "T_UserFriendEntry");

            migrationBuilder.DropForeignKey(
                name: "FK_T_UserFriendEntry_AspNetUsers_UserId",
                table: "T_UserFriendEntry");

            migrationBuilder.DropForeignKey(
                name: "FK_T_UserFriendEntry_T_FriendGroup_GroupId",
                table: "T_UserFriendEntry");

            migrationBuilder.DropPrimaryKey(
                name: "PK_T_UserFriendEntry",
                table: "T_UserFriendEntry");

            migrationBuilder.RenameTable(
                name: "T_UserFriendEntry",
                newName: "T_Friendships");

            migrationBuilder.RenameIndex(
                name: "IX_T_UserFriendEntry_GroupId",
                table: "T_Friendships",
                newName: "IX_T_Friendships_GroupId");

            migrationBuilder.RenameIndex(
                name: "IX_T_UserFriendEntry_FriendId",
                table: "T_Friendships",
                newName: "IX_T_Friendships_FriendId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_T_Friendships",
                table: "T_Friendships",
                column: "FriendshipId");

            migrationBuilder.AddForeignKey(
                name: "FK_T_Friendships_AspNetUsers_FriendId",
                table: "T_Friendships",
                column: "FriendId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_T_Friendships_AspNetUsers_UserId",
                table: "T_Friendships",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_T_Friendships_T_FriendGroup_GroupId",
                table: "T_Friendships",
                column: "GroupId",
                principalTable: "T_FriendGroup",
                principalColumn: "GroupId");
        }
    }
}
