using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;
using Pgvector;

#nullable disable

namespace AIKnowledge.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    total_files = table.Column<int>(type: "integer", nullable: false),
                    completed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    failed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "Processing"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: true),
                    collection_id = table.Column<string>(type: "text", nullable: true),
                    virtual_path = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    chunk_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "Pending"),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_indexed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    metadata = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    category = table.Column<string>(type: "text", nullable: false),
                    values = table.Column<Dictionary<string, object>>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.category);
                });

            migrationBuilder.CreateTable(
                name: "batch_documents",
                columns: table => new
                {
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_batch_documents", x => new { x.batch_id, x.document_id });
                    table.ForeignKey(
                        name: "FK_batch_documents_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_batch_documents_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    chunk_index = table.Column<int>(type: "integer", nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    start_offset = table.Column<int>(type: "integer", nullable: false),
                    end_offset = table.Column<int>(type: "integer", nullable: false),
                    metadata = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, computedColumnSql: "to_tsvector('english', content)", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunks", x => x.id);
                    table.ForeignKey(
                        name: "FK_chunks_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chunk_vectors",
                columns: table => new
                {
                    chunk_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(768)", nullable: false),
                    model_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunk_vectors", x => x.chunk_id);
                    table.ForeignKey(
                        name: "FK_chunk_vectors_chunks_chunk_id",
                        column: x => x.chunk_id,
                        principalTable: "chunks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chunk_vectors_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_batch_documents_document_id",
                table: "batch_documents",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "idx_chunk_vectors_document_id",
                table: "chunk_vectors",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "idx_chunk_vectors_embedding",
                table: "chunk_vectors",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "ivfflat")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" })
                .Annotation("Npgsql:StorageParameter:lists", 100);

            migrationBuilder.CreateIndex(
                name: "idx_chunks_document_id",
                table: "chunks",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "idx_chunks_fts",
                table: "chunks",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "batch_documents");

            migrationBuilder.DropTable(
                name: "chunk_vectors");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "batches");

            migrationBuilder.DropTable(
                name: "chunks");

            migrationBuilder.DropTable(
                name: "documents");
        }
    }
}
