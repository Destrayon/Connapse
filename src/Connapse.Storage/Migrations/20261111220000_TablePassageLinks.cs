using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class TablePassageLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "IX_container_tables_document_id",
                table: "container_tables",
                newName: "ix_container_tables_document");

            migrationBuilder.AddColumn<string>(
                name: "header_hash",
                table: "container_tables",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "hidden",
                table: "container_tables",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "page",
                table: "container_tables",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "table_number",
                table: "container_tables",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "table_passage_links",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    container_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chunk_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start = table.Column<int>(type: "integer", nullable: false),
                    length = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_table_passage_links", x => x.id);
                    table.CheckConstraint("ck_table_passage_links_span", "start >= 0 AND length > 0");
                    table.ForeignKey(
                        name: "FK_table_passage_links_chunks_chunk_id",
                        column: x => x.chunk_id,
                        principalTable: "chunks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_table_passage_links_container_tables_table_id",
                        column: x => x.table_id,
                        principalTable: "container_tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_table_passage_links_containers_container_id",
                        column: x => x.container_id,
                        principalTable: "containers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_table_passage_links_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_table_passage_links_chunk",
                table: "table_passage_links",
                column: "chunk_id");

            migrationBuilder.CreateIndex(
                name: "IX_table_passage_links_container_id",
                table: "table_passage_links",
                column: "container_id");

            migrationBuilder.CreateIndex(
                name: "ix_table_passage_links_document",
                table: "table_passage_links",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_table_passage_links_table",
                table: "table_passage_links",
                column: "table_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "table_passage_links");

            migrationBuilder.DropColumn(
                name: "header_hash",
                table: "container_tables");

            migrationBuilder.DropColumn(
                name: "hidden",
                table: "container_tables");

            migrationBuilder.DropColumn(
                name: "page",
                table: "container_tables");

            migrationBuilder.DropColumn(
                name: "table_number",
                table: "container_tables");

            migrationBuilder.RenameIndex(
                name: "ix_container_tables_document",
                table: "container_tables",
                newName: "IX_container_tables_document_id");
        }
    }
}
