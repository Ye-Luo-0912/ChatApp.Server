using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarBlobDeleteKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StorageKind",
                table: "T_AttachmentBlobDeleteJob",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "attachment");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StorageKind",
                table: "T_AttachmentBlobDeleteJob");
        }
    }
}
