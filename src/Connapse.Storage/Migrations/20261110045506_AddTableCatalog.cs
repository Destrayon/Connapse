using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddTableCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "container_tables",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    container_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    storage_name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    schema = table.Column<string>(type: "jsonb", nullable: false),
                    row_count = table.Column<long>(type: "bigint", nullable: false),
                    needs_review = table.Column<bool>(type: "boolean", nullable: false),
                    rejects = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    loaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_container_tables", x => x.id);
                    table.CheckConstraint("ck_container_tables_storage_name", "storage_name ~ '^t_[0-9a-f]{32}$'");
                    table.ForeignKey(
                        name: "FK_container_tables_containers_container_id",
                        column: x => x.container_id,
                        principalTable: "containers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_container_tables_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "retired_tables",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    container_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    retired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retired_tables", x => x.id);
                    table.ForeignKey(
                        name: "FK_retired_tables_containers_container_id",
                        column: x => x.container_id,
                        principalTable: "containers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_container_tables_container",
                table: "container_tables",
                column: "container_id");

            migrationBuilder.CreateIndex(
                name: "IX_container_tables_document_id",
                table: "container_tables",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_container_tables_storage_name",
                table: "container_tables",
                column: "storage_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_retired_tables_container_id",
                table: "retired_tables",
                column: "container_id");

            migrationBuilder.CreateIndex(
                name: "ix_retired_tables_retired_at",
                table: "retired_tables",
                column: "retired_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "container_tables");

            migrationBuilder.DropTable(
                name: "retired_tables");
        }
    }
}
