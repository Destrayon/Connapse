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

        /// <summary>
        /// Removes the database index "idx_chunk_vectors_cache_lookup" from the "chunk_vectors" table.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_chunk_vectors_cache_lookup",
                table: "chunk_vectors");
        }
    }
}
