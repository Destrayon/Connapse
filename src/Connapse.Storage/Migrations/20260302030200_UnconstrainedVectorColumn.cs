using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class UnconstrainedVectorColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_chunk_vectors_embedding",
                table: "chunk_vectors");

            migrationBuilder.AlterColumn<Vector>(
                name: "embedding",
                table: "chunk_vectors",
                type: "vector",
                nullable: false,
                oldClrType: typeof(Vector),
                oldType: "vector(768)");

            migrationBuilder.CreateIndex(
                name: "idx_chunk_vectors_model_id",
                table: "chunk_vectors",
                column: "model_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_chunk_vectors_model_id",
                table: "chunk_vectors");

            migrationBuilder.AlterColumn<Vector>(
                name: "embedding",
                table: "chunk_vectors",
                type: "vector(768)",
                nullable: false,
                oldClrType: typeof(Vector),
                oldType: "vector");

            migrationBuilder.CreateIndex(
                name: "idx_chunk_vectors_embedding",
                table: "chunk_vectors",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "ivfflat")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" })
                .Annotation("Npgsql:StorageParameter:lists", 100);
        }
    }
}
