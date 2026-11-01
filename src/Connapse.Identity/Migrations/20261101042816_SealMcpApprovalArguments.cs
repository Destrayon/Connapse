using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Identity.Migrations
{
    /// <inheritdoc />
    public partial class SealMcpApprovalArguments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "arguments_protected",
                table: "mcp_approvals",
                type: "text",
                nullable: true);

            // Requests made before CON-185 stored their full arguments as the summary, secrets included, and an
            // unsalted hash of them. The store runs the same scrub every few minutes after this, for any an older
            // instance still writes mid-deploy.
            migrationBuilder.Sql(Connapse.Identity.Services.McpApprovalStore.HideUnredactedSecrets);
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
