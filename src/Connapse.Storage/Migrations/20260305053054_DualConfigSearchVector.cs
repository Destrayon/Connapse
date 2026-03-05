using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DualConfigSearchVector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "chunks",
                type: "tsvector",
                nullable: false,
                computedColumnSql: "setweight(to_tsvector('simple', coalesce(content, '')), 'A') || setweight(to_tsvector('english', coalesce(content, '')), 'B')",
                stored: true,
                oldClrType: typeof(NpgsqlTsVector),
                oldType: "tsvector",
                oldComputedColumnSql: "to_tsvector('english', content)",
                oldStored: true);

            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_chunks_fts;");
            migrationBuilder.Sql("CREATE INDEX idx_chunks_fts ON chunks USING GIN (search_vector);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "chunks",
                type: "tsvector",
                nullable: false,
                computedColumnSql: "to_tsvector('english', content)",
                stored: true,
                oldClrType: typeof(NpgsqlTsVector),
                oldType: "tsvector",
                oldComputedColumnSql: "setweight(to_tsvector('simple', coalesce(content, '')), 'A') || setweight(to_tsvector('english', coalesce(content, '')), 'B')",
                oldStored: true);
        }
    }
}
