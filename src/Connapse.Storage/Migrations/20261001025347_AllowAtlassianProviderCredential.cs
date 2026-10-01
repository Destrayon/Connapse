using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AllowAtlassianProviderCredential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Atlassian link app is a client id (config_json) and a client secret (secret_protected)
            // with no key and no Roles Anywhere fields. Still exactly one complete shape per row.
            migrationBuilder.Sql("""
                ALTER TABLE provider_credentials DROP CONSTRAINT ck_provider_credentials_one_complete_shape;
                ALTER TABLE provider_credentials ADD CONSTRAINT ck_provider_credentials_one_complete_shape CHECK (
                  (trust_anchor_arn IS NOT NULL AND profile_arn IS NOT NULL AND role_arn IS NOT NULL
                   AND region IS NOT NULL AND certificate_pem IS NOT NULL AND private_key_protected IS NOT NULL
                   AND config_json IS NULL AND secret_protected IS NULL)
                  OR
                  (provider = 'github' AND config_json IS NOT NULL AND private_key_protected IS NOT NULL
                   AND trust_anchor_arn IS NULL AND profile_arn IS NULL AND role_arn IS NULL
                   AND region IS NULL AND certificate_pem IS NULL)
                  OR
                  (provider = 'atlassian' AND config_json IS NOT NULL AND secret_protected IS NOT NULL
                   AND private_key_protected IS NULL AND trust_anchor_arn IS NULL AND profile_arn IS NULL
                   AND role_arn IS NULL AND region IS NULL AND certificate_pem IS NULL)
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Atlassian rows cannot satisfy the older constraint, so they go with it; a rollback
            // would need the link app entered again.
            migrationBuilder.Sql("""
                ALTER TABLE provider_credentials DROP CONSTRAINT ck_provider_credentials_one_complete_shape;
                DELETE FROM provider_credentials WHERE provider = 'atlassian';
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
    }
}
