using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "scope",
                table: "memory_facts",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            // Each version keeps the audience it had: one citing a document or node was seen by everyone who could read
            // its sources, so it becomes team; one citing none was seen only by its author, so it stays personal.
            migrationBuilder.Sql("""
                UPDATE memory_facts f SET scope = 1
                WHERE EXISTS (SELECT 1 FROM memory_fact_sources s WHERE s.fact_id = f.id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "scope",
                table: "memory_facts");
        }
    }
}
