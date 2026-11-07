using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Deletes every Filesystem source and connection (CON-265), provider 1 before the enum lost it. The provider is
    /// gone, so these rows could be neither read nor synced, and SFTP reaches the same folders. A source's documents,
    /// chunks and vectors go through their cascades. Link rows have no foreign key, so they are deleted first, while
    /// the documents still say whose they were. Sources go before connections, which their foreign key restricts.
    /// </summary>
    public partial class RemoveFilesystemSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMPORARY TABLE removed_filesystem_documents ON COMMIT DROP AS
                SELECT d.id::text AS document_id
                FROM documents d
                JOIN sources s ON s.id = d.source_id
                LEFT JOIN connections c ON c.id = s.connection_id
                WHERE COALESCE(s.provider, c.provider) = 1;

                DELETE FROM link_edges WHERE document_id IN (SELECT document_id FROM removed_filesystem_documents);
                DELETE FROM link_node_facts WHERE document_id IN (SELECT document_id FROM removed_filesystem_documents);
                DELETE FROM link_node_documents WHERE document_id IN (SELECT document_id FROM removed_filesystem_documents);

                DELETE FROM sources s
                USING connections c
                WHERE c.id = s.connection_id AND c.provider = 1;

                DELETE FROM sources WHERE provider = 1;

                DELETE FROM connections WHERE provider = 1;
                """);
        }

        /// <inheritdoc />
        /// <remarks>Nothing to undo: the deleted rows are gone, and this can't bring them back.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
