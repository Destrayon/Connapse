using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Moves a saved hybrid-search weight of exactly 0.75 (the previous default) to the new default
    /// 0.65 (#668). The Search tab saves every field at once, so a stored 0.75 is almost always the old
    /// default carried along rather than a choice; any other stored value is left alone. The key is
    /// matched in any casing, as the settings readers match it, and keeps its casing; every matching key
    /// in a row is moved.
    /// </summary>
    public partial class ShiftFusionAlphaToBalanced : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE settings
                SET values = (
                        SELECT jsonb_object_agg(e.key,
                            CASE WHEN lower(e.key) = 'fusionalpha'
                                      AND jsonb_typeof(e.value) = 'number'
                                      AND (e.value #>> '{}')::numeric = 0.75
                                 THEN '0.65'::jsonb ELSE e.value END)
                        FROM jsonb_each(values) AS e),
                    updated_at = now()
                WHERE lower(category) = 'search'
                  AND jsonb_typeof(values) = 'object'
                  AND EXISTS (
                        SELECT 1 FROM jsonb_each(values) AS e
                        WHERE lower(e.key) = 'fusionalpha'
                          AND jsonb_typeof(e.value) = 'number'
                          AND (e.value #>> '{}')::numeric = 0.75);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversed: a stored 0.65 after this point can't be told apart from a chosen one.
        }
    }
}
