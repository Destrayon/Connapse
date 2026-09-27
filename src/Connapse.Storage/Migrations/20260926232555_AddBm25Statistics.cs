using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// BM25 keyword ranking in plain PostgreSQL (#548), so it runs wherever the database has no
    /// BM25 extension (Azure Flexible Server, RDS, ...).
    ///
    /// Per owner, BM25 needs the chunk count and total length (for avgdl) and, per term, how many
    /// chunks contain it (df); pruning also needs each term's highest frequency and shortest chunk.
    /// Counting rows directly on every chunk write makes concurrent ingests contend on the same
    /// common-term rows and deadlock, so the triggers only append signed deltas (one row per term
    /// per statement, so a bulk load writes one row per distinct term, not one per term per chunk) and
    /// <c>Bm25StatsFolder</c> folds them into the stats tables. Readers add any unfolded deltas, so
    /// statistics are exact at all times.
    ///
    /// search_vector gains frequency markers: a positionless lexeme "term\x1Fn" for every tier n of
    /// {2,3,4,5,6,8,11,16} that the term's frequency in the chunk reaches. The GIN index then answers
    /// "chunks where term occurs at least n times", which bounds each chunk's BM25 score, so a query
    /// fetches only chunks that could reach the top k. The separator 0x1F never comes out of the
    /// text parser, so no user query can match a marker, and ts_filter drops positionless lexemes, so
    /// nothing that reads the english (weight B) copy sees them.
    ///
    /// Terms come from the english (weight B) positions of search_vector: the simple (weight A) copy
    /// shares lexemes with it, and counting both would double every count.
    /// See docs/research/portable-bm25-postgres-2026-09-26.md and
    /// docs/research/bm25-sql-latency-parity-2026-09-27.md.
    /// </summary>
    public partial class AddBm25Statistics : Migration
    {
        internal const string SearchVectorSql =
            "setweight(to_tsvector('simple', coalesce(content, '')), 'A') || " +
            "setweight(to_tsvector('english', coalesce(content, '')), 'B') || " +
            "bm25_markers(to_tsvector('english', coalesce(content, '')))";

        private const string OldSearchVectorSql =
            "setweight(to_tsvector('simple', coalesce(content, '')), 'A') || " +
            "setweight(to_tsvector('english', coalesce(content, '')), 'B')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                -- Tokens left after english analysis, duplicates counted: Lucene's document length.
                CREATE FUNCTION bm25_text_length(content text) RETURNS integer
                LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
                    SELECT coalesce(sum(cardinality(u.positions)), 0)::integer
                    FROM unnest(to_tsvector('english', coalesce(content, ''))) u
                $$;

                -- The same count read from search_vector's english copy.
                CREATE FUNCTION bm25_doc_length(sv tsvector) RETURNS bigint
                LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
                    SELECT coalesce(sum(cardinality(u.positions)), 0)
                    FROM unnest(ts_filter(sv, '{b}')) u
                $$;

                -- Cumulative frequency-tier markers; the tiers must match Bm25Pruning.Tiers.
                CREATE FUNCTION bm25_markers(english tsvector) RETURNS tsvector
                LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
                    SELECT coalesce(array_to_tsvector(array_agg(u.lexeme || chr(31) || t)), ''::tsvector)
                    FROM unnest(english) u, unnest('{2,3,4,5,6,8,11,16}'::int[]) t
                    WHERE cardinality(u.positions) >= t
                $$;

                -- One rewrite of the table for both columns.
                DROP INDEX IF EXISTS idx_chunks_fts;
                ALTER TABLE chunks
                    DROP COLUMN search_vector,
                    ADD COLUMN search_vector tsvector NOT NULL GENERATED ALWAYS AS ({{SearchVectorSql}}) STORED,
                    ADD COLUMN bm25_length integer NOT NULL GENERATED ALWAYS AS (bm25_text_length(content)) STORED;
                CREATE INDEX idx_chunks_fts ON chunks USING GIN (search_vector);

                CREATE TABLE bm25_owner_stats (
                    owner_id uuid PRIMARY KEY,
                    n_docs bigint NOT NULL,
                    total_len bigint NOT NULL);

                -- max_tf only ever rises and min_len only ever falls: after deletes they are stale
                -- but still valid bounds, which is all pruning needs.
                CREATE TABLE bm25_term_stats (
                    owner_id uuid NOT NULL,
                    term text NOT NULL,
                    df bigint NOT NULL,
                    max_tf integer NOT NULL,
                    min_len integer NOT NULL,
                    PRIMARY KEY (owner_id, term));

                -- term IS NULL: a change to the owner's chunk count and total length.
                CREATE TABLE bm25_delta (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    owner_id uuid NOT NULL,
                    term text,
                    d_df bigint NOT NULL,
                    d_n bigint NOT NULL,
                    d_len bigint NOT NULL,
                    tf integer,
                    len integer);
                CREATE INDEX idx_bm25_delta_owner_term ON bm25_delta (owner_id, term);

                CREATE FUNCTION bm25_chunks_delta() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        -- Only rows whose terms or owner changed; a metadata update nets to nothing.
                        INSERT INTO bm25_delta (owner_id, term, d_df, d_n, d_len, tf, len)
                        SELECT o.owner_id, u.lexeme, -count(*), 0, 0, NULL::integer, NULL::integer
                        FROM old_rows o JOIN new_rows n ON n.id = o.id,
                             unnest(ts_filter(o.search_vector, '{b}')) u
                        WHERE o.search_vector IS DISTINCT FROM n.search_vector OR o.owner_id <> n.owner_id
                        GROUP BY o.owner_id, u.lexeme
                        UNION ALL
                        SELECT n.owner_id, u.lexeme, count(*), 0, 0, max(cardinality(u.positions)), min(n.bm25_length)
                        FROM old_rows o JOIN new_rows n ON n.id = o.id,
                             unnest(ts_filter(n.search_vector, '{b}')) u
                        WHERE o.search_vector IS DISTINCT FROM n.search_vector OR o.owner_id <> n.owner_id
                        GROUP BY n.owner_id, u.lexeme
                        UNION ALL
                        SELECT o.owner_id, NULL, 0, -count(*), -sum(o.bm25_length), NULL::integer, NULL::integer
                        FROM old_rows o JOIN new_rows n ON n.id = o.id
                        WHERE o.search_vector IS DISTINCT FROM n.search_vector OR o.owner_id <> n.owner_id
                        GROUP BY o.owner_id
                        UNION ALL
                        SELECT n.owner_id, NULL, 0, count(*), sum(n.bm25_length), NULL::integer, NULL::integer
                        FROM old_rows o JOIN new_rows n ON n.id = o.id
                        WHERE o.search_vector IS DISTINCT FROM n.search_vector OR o.owner_id <> n.owner_id
                        GROUP BY n.owner_id;
                    ELSIF TG_OP = 'INSERT' THEN
                        INSERT INTO bm25_delta (owner_id, term, d_df, d_n, d_len, tf, len)
                        SELECT r.owner_id, u.lexeme, count(*), 0, 0, max(cardinality(u.positions)), min(r.bm25_length)
                        FROM new_rows r, unnest(ts_filter(r.search_vector, '{b}')) u
                        GROUP BY r.owner_id, u.lexeme
                        UNION ALL
                        SELECT owner_id, NULL, 0, count(*), sum(bm25_length), NULL::integer, NULL::integer
                        FROM new_rows GROUP BY owner_id;
                    ELSE
                        INSERT INTO bm25_delta (owner_id, term, d_df, d_n, d_len, tf, len)
                        SELECT r.owner_id, u.lexeme, -count(*), 0, 0, NULL::integer, NULL::integer
                        FROM old_rows r, unnest(ts_filter(r.search_vector, '{b}')) u
                        GROUP BY r.owner_id, u.lexeme
                        UNION ALL
                        SELECT owner_id, NULL, 0, -count(*), -sum(bm25_length), NULL::integer, NULL::integer
                        FROM old_rows GROUP BY owner_id;
                    END IF;
                    RETURN NULL;
                END
                $$;

                CREATE TRIGGER bm25_chunks_insert AFTER INSERT ON chunks
                    REFERENCING NEW TABLE AS new_rows
                    FOR EACH STATEMENT EXECUTE FUNCTION bm25_chunks_delta();
                CREATE TRIGGER bm25_chunks_delete AFTER DELETE ON chunks
                    REFERENCING OLD TABLE AS old_rows
                    FOR EACH STATEMENT EXECUTE FUNCTION bm25_chunks_delta();
                CREATE TRIGGER bm25_chunks_update AFTER UPDATE ON chunks
                    REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                    FOR EACH STATEMENT EXECUTE FUNCTION bm25_chunks_delta();

                -- Existing chunks. Migrations run before the app serves traffic, so nothing writes
                -- chunks between the triggers above and this backfill.
                INSERT INTO bm25_term_stats (owner_id, term, df, max_tf, min_len)
                SELECT c.owner_id, u.lexeme, count(*), max(cardinality(u.positions)), min(c.bm25_length)
                FROM chunks c, unnest(ts_filter(c.search_vector, '{b}')) u
                GROUP BY c.owner_id, u.lexeme;

                INSERT INTO bm25_owner_stats (owner_id, n_docs, total_len)
                SELECT owner_id, count(*), sum(bm25_length)
                FROM chunks GROUP BY owner_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                DROP TRIGGER IF EXISTS bm25_chunks_insert ON chunks;
                DROP TRIGGER IF EXISTS bm25_chunks_delete ON chunks;
                DROP TRIGGER IF EXISTS bm25_chunks_update ON chunks;
                DROP FUNCTION IF EXISTS bm25_chunks_delta();
                DROP TABLE IF EXISTS bm25_delta;
                DROP TABLE IF EXISTS bm25_term_stats;
                DROP TABLE IF EXISTS bm25_owner_stats;

                DROP INDEX IF EXISTS idx_chunks_fts;
                ALTER TABLE chunks
                    DROP COLUMN bm25_length,
                    DROP COLUMN search_vector,
                    ADD COLUMN search_vector tsvector NOT NULL GENERATED ALWAYS AS ({{OldSearchVectorSql}}) STORED;
                CREATE INDEX idx_chunks_fts ON chunks USING GIN (search_vector);

                DROP FUNCTION IF EXISTS bm25_markers(tsvector);
                DROP FUNCTION IF EXISTS bm25_doc_length(tsvector);
                DROP FUNCTION IF EXISTS bm25_text_length(text);
                """);
        }
    }
}
