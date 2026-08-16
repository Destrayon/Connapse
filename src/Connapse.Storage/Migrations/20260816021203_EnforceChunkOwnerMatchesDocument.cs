using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Ties a chunk's denormalized owner to its document's, so the two cannot disagree.
    /// <para>
    /// <c>documents.owner_id</c> is a generated column and cannot lie. The copies on
    /// <c>chunks</c> and <c>chunk_vectors</c> are plain columns, and search filters on them —
    /// so a chunk carrying the wrong owner is returned to whoever owns that id, which is the
    /// cross-owner content exposure this whole epic exists to prevent. Until now a single
    /// write path kept them in step by convention; #351 adds a second owner kind, so the
    /// invariant is moved into the database instead.
    /// </para>
    /// <para>
    /// Replaces the single-column document FKs rather than adding to them: the composite
    /// form still cascades deletes, and a second FK on the same column would only duplicate
    /// the check.
    /// </para>
    /// </summary>
    public partial class EnforceChunkOwnerMatchesDocument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Composite FKs need a unique constraint covering exactly these columns. id is
            // already the primary key, so this is unique by construction — it exists to give
            // the FK something to reference, not to constrain anything new.
            migrationBuilder.Sql("""
                ALTER TABLE documents
                ADD CONSTRAINT uq_documents_id_owner UNIQUE (id, owner_id);
                """);

            // Quoted: EF created these with a capitalised prefix, and an unquoted identifier
            // would fold to lower case and silently match nothing.
            migrationBuilder.Sql("""ALTER TABLE chunks DROP CONSTRAINT IF EXISTS "FK_chunks_documents_document_id";""");
            migrationBuilder.Sql("""ALTER TABLE chunk_vectors DROP CONSTRAINT IF EXISTS "FK_chunk_vectors_documents_document_id";""");

            // NOT VALID skips the scan of existing rows but still enforces every INSERT and
            // UPDATE from here on, which is what matters: the second write path is what this
            // release introduces. A validating constraint would abort startup for every
            // operator if any legacy row diverged, taking down uncorrupted deployments to
            // report a condition the GUID-preserving #350 and #359 migrations should have made
            // impossible. Validate in a later release, once that can be confirmed against real
            // data rather than assumed:
            //   ALTER TABLE chunks VALIDATE CONSTRAINT fk_chunks_document_owner;
            migrationBuilder.Sql("""
                ALTER TABLE chunks
                ADD CONSTRAINT fk_chunks_document_owner
                FOREIGN KEY (document_id, owner_id)
                REFERENCES documents (id, owner_id)
                ON DELETE CASCADE
                NOT VALID;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE chunk_vectors
                ADD CONSTRAINT fk_chunk_vectors_document_owner
                FOREIGN KEY (document_id, owner_id)
                REFERENCES documents (id, owner_id)
                ON DELETE CASCADE
                NOT VALID;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE chunks DROP CONSTRAINT IF EXISTS fk_chunks_document_owner;");
            migrationBuilder.Sql("ALTER TABLE chunk_vectors DROP CONSTRAINT IF EXISTS fk_chunk_vectors_document_owner;");

            // Restore the single-column cascades this migration replaced, so rolling back does
            // not leave chunks without any FK to their document.
            migrationBuilder.Sql("""
                ALTER TABLE chunks
                ADD CONSTRAINT "FK_chunks_documents_document_id"
                FOREIGN KEY (document_id) REFERENCES documents (id) ON DELETE CASCADE;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE chunk_vectors
                ADD CONSTRAINT "FK_chunk_vectors_documents_document_id"
                FOREIGN KEY (document_id) REFERENCES documents (id) ON DELETE CASCADE;
                """);

            migrationBuilder.Sql("ALTER TABLE documents DROP CONSTRAINT IF EXISTS uq_documents_id_owner;");
        }
    }
}
