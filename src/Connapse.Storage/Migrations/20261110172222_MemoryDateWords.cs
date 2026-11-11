using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// A date in a memory fact's value also reads as its month and year ("2027-01-19" as "January Jan 2027"), so a recall
    /// for "January" finds it. A stored generated column can't change its expression before PostgreSQL 17, so the column
    /// is dropped and added again (refilling it from each row), and its GIN index with it.
    /// </summary>
    public partial class MemoryDateWords : Migration
    {
        private const string Before =
            "setweight(to_tsvector('english', regexp_replace(subject, '[^[:alnum:]]+', ' ', 'g')), 'A') || "
            + "setweight(to_tsvector('english', regexp_replace(attribute, '[^[:alnum:]]+', ' ', 'g')), 'B') || "
            + "setweight(to_tsvector('english', regexp_replace(value, '[^[:alnum:]]+', ' ', 'g')), 'C') || "
            + "setweight(to_tsvector('english', regexp_replace(coalesce(reason, ''), '[^[:alnum:]]+', ' ', 'g')), 'D')";

        private const string After =
            "setweight(to_tsvector('english', regexp_replace(subject, '[^[:alnum:]]+', ' ', 'g')), 'A') || "
            + "setweight(to_tsvector('english', regexp_replace(attribute, '[^[:alnum:]]+', ' ', 'g')), 'B') || "
            + "setweight(to_tsvector('english', regexp_replace(value, '[^[:alnum:]]+', ' ', 'g')), 'C') || "
            + "setweight(to_tsvector('english', memory_date_words(value)), 'C') || "
            + "setweight(to_tsvector('english', regexp_replace(coalesce(reason, ''), '[^[:alnum:]]+', ' ', 'g')), 'D')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Each year-month (with or without a day) not inside a longer number becomes "<Month> <Mon> <year>". IMMUTABLE
            // holds: the month names are spelled out here, not taken from the session's locale.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION memory_date_words(t text) RETURNS text
                LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
                    SELECT coalesce(string_agg(
                        (ARRAY['January','February','March','April','May','June','July','August','September','October','November','December'])[m[2]::int]
                        || ' ' || (ARRAY['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'])[m[2]::int]
                        || ' ' || m[1], ' '), '')
                    FROM regexp_matches(coalesce(t, ''), '(?<![0-9])((?:19|20)[0-9]{2})[-/.](0[1-9]|1[0-2])(?![0-9])', 'g') AS m
                $$;
                """);
            Replace(migrationBuilder, After);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Replace(migrationBuilder, Before);
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS memory_date_words(text);");
        }

        private static void Replace(MigrationBuilder migrationBuilder, string expression)
        {
            migrationBuilder.Sql("ALTER TABLE memory_facts DROP COLUMN search_vector;");
            migrationBuilder.Sql($"ALTER TABLE memory_facts ADD COLUMN search_vector tsvector NOT NULL GENERATED ALWAYS AS ({expression}) STORED;");
            migrationBuilder.Sql("CREATE INDEX idx_memory_facts_fts ON memory_facts USING GIN (search_vector);");
        }
    }
}
