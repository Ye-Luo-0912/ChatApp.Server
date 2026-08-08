using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AttachmentConfirmSagaAndDeletionEpoch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ScanVersion",
                table: "T_AttachmentScanProjection",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "UploaderDeletionEpoch",
                table: "T_AttachmentScanProjection",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "UploaderDeletionEpoch",
                table: "T_AttachmentScanJob",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "DeletionEpoch",
                table: "AspNetUsers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "T_AttachmentConfirmSaga",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AttachmentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProtectedTicket = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    UploaderDeletionEpoch = table.Column<long>(type: "bigint", nullable: false),
                    ConfirmedObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    OriginalName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ScanJobId = table.Column<long>(type: "bigint", nullable: true),
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
                    table.PrimaryKey("PK_T_AttachmentConfirmSaga", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentConfirmSaga_Due",
                table: "T_AttachmentConfirmSaga",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentConfirmSaga_LeaseDue",
                table: "T_AttachmentConfirmSaga",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentConfirmSaga_UserId",
                table: "T_AttachmentConfirmSaga",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UX_AttachmentConfirmSaga_AttachmentId",
                table: "T_AttachmentConfirmSaga",
                column: "AttachmentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "T_AttachmentConfirmSaga");

            migrationBuilder.DropColumn(
                name: "ScanVersion",
                table: "T_AttachmentScanProjection");

            migrationBuilder.DropColumn(
                name: "UploaderDeletionEpoch",
                table: "T_AttachmentScanProjection");

            migrationBuilder.DropColumn(
                name: "UploaderDeletionEpoch",
                table: "T_AttachmentScanJob");

            migrationBuilder.DropColumn(
                name: "DeletionEpoch",
                table: "AspNetUsers");
        }
    }
}
