using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkVectorCacheLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_chunk_vectors_cache_lookup",
                table: "chunk_vectors",
                columns: new[] { "content_hash", "model_id", "dimensions" },
                filter: "\"content_hash\" IS NOT NULL AND \"dimensions\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_chunk_vectors_cache_lookup",
                table: "chunk_vectors");
        }
    }
}
