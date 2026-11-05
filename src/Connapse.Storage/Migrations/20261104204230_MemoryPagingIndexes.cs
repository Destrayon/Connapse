using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class MemoryPagingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_memory_facts_chain",
                table: "memory_facts");

            migrationBuilder.CreateIndex(
                name: "idx_memory_facts_chain",
                table: "memory_facts",
                columns: new[] { "chain_id", "recorded_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "idx_memory_facts_current",
                table: "memory_facts",
                columns: new[] { "container_id", "recorded_at", "id" },
                descending: new[] { false, true, true },
                filter: "superseded_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_memory_facts_chain",
                table: "memory_facts");

            migrationBuilder.DropIndex(
                name: "idx_memory_facts_current",
                table: "memory_facts");

            migrationBuilder.CreateIndex(
                name: "idx_memory_facts_chain",
                table: "memory_facts",
                column: "chain_id");
        }
    }
}
