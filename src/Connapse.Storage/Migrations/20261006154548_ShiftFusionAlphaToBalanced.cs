using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Moves a saved hybrid-search weight of exactly 0.75 (the previous default) to the new default
    /// 0.65 (#668). The Search tab saves every field at once, so a stored 0.75 is almost always the old
    /// default carried along rather than a choice; any other stored value is left alone. The key is
    /// matched in any casing, as the settings readers match it, and keeps its casing.
    /// </summary>
    public partial class ShiftFusionAlphaToBalanced : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE settings AS s
                SET values = jsonb_set(s.values, ARRAY[k.key], '0.65'::jsonb), updated_at = now()
                FROM settings AS src
                CROSS JOIN LATERAL jsonb_each(src.values) AS k(key, value)
                WHERE s.category = src.category
                  AND lower(src.category) = 'search'
                  AND lower(k.key) = 'fusionalpha'
                  AND jsonb_typeof(k.value) = 'number'
                  AND (k.value #>> '{}')::numeric = 0.75;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversed: a stored 0.65 after this point can't be told apart from a chosen one.
        }
    }
}
