using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentIngestionState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ingestion_state",
                table: "documents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.CreateIndex(
                name: "ix_documents_ingestion_state",
                table: "documents",
                column: "ingestion_state");

            // Backfill existing rows from prior signals:
            //   - Docs with a stored summary → SummaryIndexed
            //   - Docs without a summary but with chunks → Indexed
            //   - Docs with no chunks stay 'Pending' (the column DEFAULT)
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    UPDATE documents SET ingestion_state = 'SummaryIndexed'
                      WHERE summary IS NOT NULL;

                    UPDATE documents SET ingestion_state = 'Indexed'
                      WHERE summary IS NULL
                        AND EXISTS (SELECT 1 FROM chunks WHERE chunks.document_id = documents.id);
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_documents_ingestion_state",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ingestion_state",
                table: "documents");
        }
    }
}
