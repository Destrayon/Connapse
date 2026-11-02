using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Identity.Migrations
{
    /// <summary>
    /// Drops agent:ingest from stored agent keys and personal access tokens (CON-171). No check ever
    /// required it: uploads and deletes need knowledge:write on every surface, so removing it changes
    /// nothing a token can do, and a key's listing stops naming a scope that grants nothing.
    /// </summary>
    public partial class RemoveAgentIngestScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (string table in new[] { "agent_api_keys", "personal_access_tokens" })
            {
                migrationBuilder.Sql($"""
                    UPDATE {table}
                    SET scopes = array_to_string(array(
                        SELECT trim(s) FROM unnest(string_to_array(scopes, ',')) WITH ORDINALITY AS t(s, n)
                        WHERE trim(s) <> 'agent:ingest' AND trim(s) <> ''
                        ORDER BY n), ',')
                    WHERE ',' || replace(scopes, ' ', '') || ',' LIKE '%,agent:ingest,%';
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversed: the dropped scope granted nothing, and which tokens held it isn't kept.
        }
    }
}
