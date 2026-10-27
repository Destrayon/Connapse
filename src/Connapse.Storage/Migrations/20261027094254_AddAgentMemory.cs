using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memory_facts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    chain_id = table.Column<Guid>(type: "uuid", nullable: false),
                    container_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    attribute = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    superseded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replaces_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, computedColumnSql: "setweight(to_tsvector('english', regexp_replace(subject, '[^[:alnum:]]+', ' ', 'g')), 'A') || setweight(to_tsvector('english', regexp_replace(attribute, '[^[:alnum:]]+', ' ', 'g')), 'B') || setweight(to_tsvector('english', regexp_replace(value, '[^[:alnum:]]+', ' ', 'g')), 'C') || setweight(to_tsvector('english', regexp_replace(coalesce(reason, ''), '[^[:alnum:]]+', ' ', 'g')), 'D')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_facts", x => x.id);
                    table.ForeignKey(
                        name: "FK_memory_facts_containers_container_id",
                        column: x => x.container_id,
                        principalTable: "containers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memory_fact_sources",
                columns: table => new
                {
                    fact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_fact_sources", x => new { x.fact_id, x.ordinal });
                    table.ForeignKey(
                        name: "FK_memory_fact_sources_memory_facts_fact_id",
                        column: x => x.fact_id,
                        principalTable: "memory_facts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_memory_facts_chain",
                table: "memory_facts",
                column: "chain_id");

            migrationBuilder.CreateIndex(
                name: "idx_memory_facts_fts",
                table: "memory_facts",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "idx_memory_facts_key",
                table: "memory_facts",
                columns: new[] { "container_id", "subject", "attribute" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_fact_sources");

            migrationBuilder.DropTable(
                name: "memory_facts");
        }
    }
}
