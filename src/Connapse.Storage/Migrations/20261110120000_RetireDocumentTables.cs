using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// A file's table goes when its document does (CON-302). The trigger fires on every document delete, whichever
    /// code path makes it, and hands the table to retired_tables for the maintenance job to drop. When the container
    /// itself is being deleted its row is already gone, so nothing is retired: the container's schema drop takes the
    /// table with it.
    /// </summary>
    public partial class RetireDocumentTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION retire_document_tables() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO retired_tables (container_id, storage_name)
                    SELECT t.container_id, t.storage_name
                    FROM container_tables t
                    WHERE t.document_id = OLD.id
                      AND EXISTS (SELECT 1 FROM containers c WHERE c.id = t.container_id);
                    DELETE FROM container_tables WHERE document_id = OLD.id;
                    RETURN OLD;
                END
                $$;

                CREATE TRIGGER trg_documents_retire_tables
                    BEFORE DELETE ON documents
                    FOR EACH ROW EXECUTE FUNCTION retire_document_tables();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_documents_retire_tables ON documents;
                DROP FUNCTION IF EXISTS retire_document_tables();
                """);
        }
    }
}
