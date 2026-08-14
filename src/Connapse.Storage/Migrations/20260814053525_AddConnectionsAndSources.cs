using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionsAndSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_documents_container_path",
                table: "documents");

            migrationBuilder.RenameColumn(
                name: "container_id",
                table: "chunks",
                newName: "owner_id");

            migrationBuilder.RenameIndex(
                name: "idx_chunks_container_id",
                table: "chunks",
                newName: "idx_chunks_owner_id");

            migrationBuilder.RenameColumn(
                name: "container_id",
                table: "chunk_vectors",
                newName: "owner_id");

            migrationBuilder.RenameIndex(
                name: "idx_chunk_vectors_container_id",
                table: "chunk_vectors",
                newName: "idx_chunk_vectors_owner_id");

            migrationBuilder.AlterColumn<Guid>(
                name: "container_id",
                table: "documents",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "source_id",
                table: "documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_id",
                table: "documents",
                type: "uuid",
                nullable: false,
                computedColumnSql: "COALESCE(container_id, source_id)",
                stored: true);

            migrationBuilder.CreateTable(
                name: "connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    config = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    secret_protected = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    settings_overrides = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    sync_cursor = table.Column<string>(type: "text", nullable: true),
                    last_synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_sync_status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_sync_error = table.Column<string>(type: "text", nullable: true),
                    sync_interval_seconds = table.Column<int>(type: "integer", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    summary_generated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    summary_doc_set_hash = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sources", x => x.id);
                    table.ForeignKey(
                        name: "FK_sources_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_documents_owner_path",
                table: "documents",
                columns: new[] { "owner_id", "path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_documents_source_id",
                table: "documents",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_owner_id",
                table: "documents",
                column: "owner_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_documents_single_owner",
                table: "documents",
                sql: "(container_id IS NULL) <> (source_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_connections_name",
                table: "connections",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sources_connection_id",
                table: "sources",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_sources_name",
                table: "sources",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_documents_sources_source_id",
                table: "documents",
                column: "source_id",
                principalTable: "sources",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuse the rollback before dropping anything. Source-owned documents have a
            // null container_id and no container to revert to, so restoring the NOT NULL
            // constraint would either rewrite their ownership to the zero GUID or fail
            // obscurely on the containers foreign key. Fail fast with an actionable message
            // instead, while the schema is still intact.
            migrationBuilder.Sql("""
                DO $$
                DECLARE source_owned_count bigint;
                BEGIN
                    SELECT count(*) INTO source_owned_count FROM documents WHERE source_id IS NOT NULL;
                    IF source_owned_count > 0 THEN
                        RAISE EXCEPTION
                            'Cannot roll back AddConnectionsAndSources: % document(s) are owned by a source and have no container to revert to. Delete or repoint them before rolling back.',
                            source_owned_count;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_documents_sources_source_id",
                table: "documents");

            migrationBuilder.DropTable(
                name: "sources");

            migrationBuilder.DropTable(
                name: "connections");

            migrationBuilder.DropIndex(
                name: "idx_documents_owner_path",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "idx_documents_source_id",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "ix_documents_owner_id",
                table: "documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_documents_single_owner",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "owner_id",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "source_id",
                table: "documents");

            migrationBuilder.RenameColumn(
                name: "owner_id",
                table: "chunks",
                newName: "container_id");

            migrationBuilder.RenameIndex(
                name: "idx_chunks_owner_id",
                table: "chunks",
                newName: "idx_chunks_container_id");

            migrationBuilder.RenameColumn(
                name: "owner_id",
                table: "chunk_vectors",
                newName: "container_id");

            migrationBuilder.RenameIndex(
                name: "idx_chunk_vectors_owner_id",
                table: "chunk_vectors",
                newName: "idx_chunk_vectors_container_id");

            migrationBuilder.AlterColumn<Guid>(
                name: "container_id",
                table: "documents",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_documents_container_path",
                table: "documents",
                columns: new[] { "container_id", "path" },
                unique: true);
        }
    }
}
