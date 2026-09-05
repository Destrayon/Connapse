using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderCredentialShapeConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE provider_credentials ADD CONSTRAINT ck_provider_credentials_single_shape CHECK (
                  (trust_anchor_arn IS NOT NULL AND profile_arn IS NOT NULL AND role_arn IS NOT NULL
                   AND region IS NOT NULL AND certificate_pem IS NOT NULL AND private_key_protected IS NOT NULL
                   AND public_id = '' AND secret_protected = '')
                  OR
                  (trust_anchor_arn IS NULL AND profile_arn IS NULL AND role_arn IS NULL
                   AND region IS NULL AND certificate_pem IS NULL AND private_key_protected IS NULL
                   AND public_id <> '' AND secret_protected <> '')
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE provider_credentials DROP CONSTRAINT ck_provider_credentials_single_shape;");
        }
    }
}
