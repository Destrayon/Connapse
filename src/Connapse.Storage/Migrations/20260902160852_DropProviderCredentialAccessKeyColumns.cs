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
            // Delete every non-Roles-Anywhere row BEFORE the columns go. Under the old single-shape
            // CHECK, a null trust anchor means an access-key row — a credential this change retires.
            // Left in place it would become a fail-open trap: its access-key columns vanish, the
            // runtime reads the null trust anchor as "nothing configured" and silently falls through
            // to ambient AWS credentials, while the status page still sees the row. Removing it makes
            // a retired credential read honestly as "not configured" and prompts a fresh Roles
            // Anywhere setup. (Pre-release: at most a stray dev/test row; nothing in use is lost.)
            migrationBuilder.Sql(
                "DELETE FROM provider_credentials WHERE trust_anchor_arn IS NULL;");

            // The single-shape CHECK references public_id/secret_protected, so it must be dropped
            // before the columns it constrains.
            migrationBuilder.Sql(
                "ALTER TABLE provider_credentials DROP CONSTRAINT ck_provider_credentials_single_shape;");

            migrationBuilder.DropColumn(
                name: "public_id",
                table: "provider_credentials");

            migrationBuilder.DropColumn(
                name: "secret_protected",
                table: "provider_credentials");

            // Roles Anywhere is now the only shape, so make completeness a database invariant: a row
            // must carry the whole configuration. This closes the fail-open gap at the source — a
            // half-written or tampered row can never exist to be misread as "nothing configured".
            migrationBuilder.Sql("""
                ALTER TABLE provider_credentials ADD CONSTRAINT ck_provider_credentials_roles_anywhere_complete CHECK (
                  trust_anchor_arn IS NOT NULL AND profile_arn IS NOT NULL AND role_arn IS NOT NULL
                  AND region IS NOT NULL AND certificate_pem IS NOT NULL AND private_key_protected IS NOT NULL
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rows deleted in Up are not restored — a rollback recovers the schema, not retired
            // access-key credentials, which would have to be entered again.
            migrationBuilder.Sql(
                "ALTER TABLE provider_credentials DROP CONSTRAINT ck_provider_credentials_roles_anywhere_complete;");

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
            // columns it references exist again. The surviving Roles Anywhere rows now carry
            // public_id='' / secret_protected='' (the defaults above), which is its Roles Anywhere branch.
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
