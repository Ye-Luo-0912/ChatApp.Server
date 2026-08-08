using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarFinalizationSaga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "T_AvatarFinalizationSaga",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProtectedTicket = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    OldAvatarUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ExpectedAvatarVersion = table.Column<long>(type: "bigint", nullable: false),
                    UploaderDeletionEpoch = table.Column<long>(type: "bigint", nullable: false),
                    FinalObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PublicUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_T_AvatarFinalizationSaga", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AvatarFinalizationSaga_Due",
                table: "T_AvatarFinalizationSaga",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AvatarFinalizationSaga_LeaseDue",
                table: "T_AvatarFinalizationSaga",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AvatarFinalizationSaga_UserId",
                table: "T_AvatarFinalizationSaga",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UX_AvatarFinalizationSaga_User_Object",
                table: "T_AvatarFinalizationSaga",
                columns: new[] { "UserId", "ObjectKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "T_AvatarFinalizationSaga");
        }
    }
}
