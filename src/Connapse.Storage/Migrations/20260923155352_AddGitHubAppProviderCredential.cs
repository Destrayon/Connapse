using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubAppProviderCredential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "config_json",
                table: "provider_credentials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "secret_protected",
                table: "provider_credentials",
                type: "text",
                nullable: true);

            // A row is still exactly one complete credential — never half of one, which could be
            // misread as "nothing configured" — but that credential may now be a GitHub App as well
            // as a Roles Anywhere configuration.
            migrationBuilder.Sql("""
                ALTER TABLE provider_credentials DROP CONSTRAINT ck_provider_credentials_roles_anywhere_complete;
                ALTER TABLE provider_credentials ADD CONSTRAINT ck_provider_credentials_one_complete_shape CHECK (
                  (trust_anchor_arn IS NOT NULL AND profile_arn IS NOT NULL AND role_arn IS NOT NULL
                   AND region IS NOT NULL AND certificate_pem IS NOT NULL AND private_key_protected IS NOT NULL
                   AND config_json IS NULL AND secret_protected IS NULL)
                  OR
                  (provider = 'github' AND config_json IS NOT NULL AND private_key_protected IS NOT NULL
                   AND trust_anchor_arn IS NULL AND profile_arn IS NULL AND role_arn IS NULL
                   AND region IS NULL AND certificate_pem IS NULL)
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // GitHub App rows cannot satisfy the older constraint, so they go with the columns; a
            // rollback would need the App set up again.
            migrationBuilder.Sql("""
                ALTER TABLE provider_credentials DROP CONSTRAINT ck_provider_credentials_one_complete_shape;
                DELETE FROM provider_credentials WHERE provider = 'github';
                ALTER TABLE provider_credentials ADD CONSTRAINT ck_provider_credentials_roles_anywhere_complete CHECK (
                  trust_anchor_arn IS NOT NULL AND profile_arn IS NOT NULL AND role_arn IS NOT NULL
                  AND region IS NOT NULL AND certificate_pem IS NOT NULL AND private_key_protected IS NOT NULL
                );
                """);

            migrationBuilder.DropColumn(
                name: "config_json",
                table: "provider_credentials");

            migrationBuilder.DropColumn(
                name: "secret_protected",
                table: "provider_credentials");
        }
    }
}
