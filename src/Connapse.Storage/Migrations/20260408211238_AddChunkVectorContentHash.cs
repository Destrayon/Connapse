using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkVectorContentHash : Migration
    {
        /// <summary>
        /// Adds nullable `content_hash` (text) and `dimensions` (integer) columns to the `chunk_vectors` table.
        /// </summary>
        /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> used to apply the schema changes.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_hash",
                table: "chunk_vectors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "dimensions",
                table: "chunk_vectors",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "content_hash",
                table: "chunk_vectors");

            migrationBuilder.DropColumn(
                name: "dimensions",
                table: "chunk_vectors");
        }
    }
}
