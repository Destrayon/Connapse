using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DropProviderCredentialAccessKeyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The single-shape CHECK references public_id/secret_protected, so it must be dropped
            // before the columns it constrains. Roles Anywhere is now the only stored shape, so
            // there is nothing left for the constraint to enforce.
            migrationBuilder.Sql(
                "ALTER TABLE provider_credentials DROP CONSTRAINT ck_provider_credentials_single_shape;");

            migrationBuilder.DropColumn(
                name: "public_id",
                table: "provider_credentials");

            migrationBuilder.DropColumn(
                name: "secret_protected",
                table: "provider_credentials");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "public_id",
                table: "provider_credentials",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "secret_protected",
                table: "provider_credentials",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Reinstate the single-shape CHECK exactly as 20260901225414 created it — added after the
            // columns it references exist again.
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
    }
}
