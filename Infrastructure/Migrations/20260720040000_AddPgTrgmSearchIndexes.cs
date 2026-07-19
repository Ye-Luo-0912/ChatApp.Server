using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPgTrgmSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_AspNetUsers_UserName_trgm"
                ON "AspNetUsers" USING gin ("UserName" gin_trgm_ops);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_T_UserFriendEntry_Note_trgm"
                ON "T_UserFriendEntry" USING gin ("Note" gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_T_UserFriendEntry_Note_trgm\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_AspNetUsers_UserName_trgm\";");
        }
    }
}
