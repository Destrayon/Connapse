using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Identity.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAgentIdentityRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remove the Agent role from the identity system — agents are now
            // first-class entities in the agents table, not ConnapseUser + role assignments.
            migrationBuilder.Sql("""
                DELETE FROM user_roles
                WHERE role_id IN (SELECT id FROM roles WHERE name = 'Agent');

                DELETE FROM roles WHERE name = 'Agent';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-insert the Agent role on rollback (without user assignments)
            migrationBuilder.Sql("""
                INSERT INTO roles (id, name, normalized_name, concurrency_stamp, description, created_at)
                SELECT gen_random_uuid(), 'Agent', 'AGENT', gen_random_uuid(), 'Programmatic access for AI agents (read + ingest)', now()
                WHERE NOT EXISTS (SELECT 1 FROM roles WHERE name = 'Agent');
                """);
        }
    }
}
