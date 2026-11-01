using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Identity.Migrations
{
    /// <inheritdoc />
    public partial class SealMcpApprovalArguments : Migration
    {
        // Requests made before CON-185 stored their full arguments as the summary, secrets included. A decided
        // or expired one is never shown again, so its copy goes. A pending one keeps its hash, so approving it
        // still lets the call run; its summary is replaced if any field name could be a secret. The match strips
        // '_' and '-' and lowercases, like McpCallArguments.IsSecret, and also hits values, so it hides more, never less.
        public const string HideLegacySecrets = """
            UPDATE mcp_approvals SET arguments_summary = ''
            WHERE status <> 'Pending' OR expires_at <= now();

            UPDATE mcp_approvals
            SET arguments_summary = '(hidden: requested before secret fields were redacted. Approving still runs the call; deny it and repeat the call to see its other arguments.)'
            WHERE status = 'Pending' AND expires_at > now()
              AND regexp_replace(lower(arguments_summary), '[-_]', '', 'g')
                  ~ '(secret|password|passphrase|token|apikey|privatekey|privatekeypem|connectionstring|authorization|credential|credentials|cookie)"';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "arguments_protected",
                table: "mcp_approvals",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(HideLegacySecrets);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "arguments_protected",
                table: "mcp_approvals");
        }
    }
}
