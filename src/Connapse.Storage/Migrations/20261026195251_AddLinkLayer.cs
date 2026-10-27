using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "link_edges",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    edge_id = table.Column<string>(type: "text", nullable: false),
                    from_node = table.Column<string>(type: "text", nullable: false),
                    to_node = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    document_id = table.Column<string>(type: "text", nullable: false),
                    evidence = table.Column<string>(type: "text", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link_edges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "link_node_documents",
                columns: table => new
                {
                    node_id = table.Column<string>(type: "text", nullable: false),
                    document_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link_node_documents", x => new { x.node_id, x.document_id });
                });

            migrationBuilder.CreateTable(
                name: "link_node_facts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    node_id = table.Column<string>(type: "text", nullable: false),
                    field = table.Column<short>(type: "smallint", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    document_id = table.Column<string>(type: "text", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link_node_facts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_link_edges_document",
                table: "link_edges",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "idx_link_edges_from",
                table: "link_edges",
                columns: new[] { "from_node", "type" });

            migrationBuilder.CreateIndex(
                name: "idx_link_edges_to",
                table: "link_edges",
                columns: new[] { "to_node", "type" });

            migrationBuilder.CreateIndex(
                name: "idx_link_node_documents_document",
                table: "link_node_documents",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "idx_link_node_facts_document",
                table: "link_node_facts",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "idx_link_node_facts_node",
                table: "link_node_facts",
                column: "node_id");

            // Link rows name their document as text, so no foreign key can cascade. A statement-level trigger
            // removes what deleted documents asserted, however they were deleted (a container or source going
            // away cascades to its documents and fires this too).
            migrationBuilder.Sql("""
                CREATE FUNCTION delete_document_links() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    DELETE FROM link_edges WHERE document_id IN (SELECT id::text FROM gone);
                    DELETE FROM link_node_facts WHERE document_id IN (SELECT id::text FROM gone);
                    DELETE FROM link_node_documents WHERE document_id IN (SELECT id::text FROM gone);
                    RETURN NULL;
                END $$;

                CREATE TRIGGER documents_delete_links
                    AFTER DELETE ON documents
                    REFERENCING OLD TABLE AS gone
                    FOR EACH STATEMENT EXECUTE FUNCTION delete_document_links();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS documents_delete_links ON documents;
                DROP FUNCTION IF EXISTS delete_document_links();
                """);

            migrationBuilder.DropTable(
                name: "link_edges");

            migrationBuilder.DropTable(
                name: "link_node_documents");

            migrationBuilder.DropTable(
                name: "link_node_facts");
        }
    }
}
