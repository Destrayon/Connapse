using System.Diagnostics;
using Connapse.Storage.Tables;
using CsCheck;
using FluentAssertions;

namespace Connapse.Storage.Tests.Tables;

/// <summary>
/// The agent query check (CON-325). The bypass tests follow the parser rows of the bypass table in
/// docs/research/rls-raw-sql-agents-2026-11-10.md section 3, one test per row; the rest cover each rule's branches.
/// </summary>
[Trait("Category", "Unit")]
public class TableSqlCheckTests
{
    private static readonly TableSqlScope Scope = new("tables_abc", new HashSet<string> { "t_1", "t_2", "pg_x" });

    private static TableSqlVerdict Check(string sql) => TableSqlCheck.Check(sql, Scope);

    private static void Refused(string sql, TableSqlRefusal reason)
    {
        var verdict = Check(sql);
        verdict.Allowed.Should().BeFalse(sql);
        verdict.Refusal.Should().Be(reason, sql);
        verdict.Detail.Should().NotBeEmpty();
    }

    private static void Allowed(string sql)
    {
        var verdict = Check(sql);
        verdict.Refusal.Should().Be(TableSqlRefusal.None, $"{sql} ({verdict.Detail})");
        verdict.Allowed.Should().BeTrue();
    }

    // ---- Bypass table rows -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("COMMIT; INSERT INTO t_1 VALUES (1)")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1; SET default_transaction_read_only = off")]
    public void Check_StackedStatements_Refused(string sql) => Refused(sql, TableSqlRefusal.MultipleStatements);

    [Theory]
    [InlineData("SET default_transaction_read_only = off")]
    [InlineData("SET TRANSACTION READ WRITE")]
    [InlineData("SET SESSION CHARACTERISTICS AS TRANSACTION READ WRITE")]
    [InlineData("RESET default_transaction_read_only")]
    public void Check_TurningReadOnlyOff_Refused(string sql) => Refused(sql, TableSqlRefusal.NotASelect);

    [Theory]
    [InlineData("SELECT set_config('tables_auth.token', 'forged', false)")]
    [InlineData("SELECT current_setting('tables_auth.token')")]
    [InlineData("SELECT * FROM t_1 WHERE c_1 = current_setting('tables_auth.user')")]
    public void Check_RequestTokenFunctions_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedFunction);

    [Theory]
    [InlineData("SET ROLE postgres")]
    [InlineData("RESET ROLE")]
    [InlineData("SET SESSION AUTHORIZATION postgres")]
    [InlineData("RESET SESSION AUTHORIZATION")]
    public void Check_RoleSwitch_Refused(string sql) => Refused(sql, TableSqlRefusal.NotASelect);

    [Theory]
    [InlineData("SELECT current_user")]
    [InlineData("SELECT session_user")]
    [InlineData("SELECT current_role")]
    [InlineData("SELECT user")]
    [InlineData("SELECT current_schema")]
    [InlineData("SELECT current_catalog")]
    public void Check_IdentityKeywords_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedFunction);

    [Theory]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$")]
    [InlineData("CREATE FUNCTION f() RETURNS int AS 'SELECT 1' LANGUAGE sql")]
    [InlineData("CALL p()")]
    public void Check_ProceduralCode_Refused(string sql) => Refused(sql, TableSqlRefusal.NotASelect);

    [Theory]
    [InlineData("SELECT * FROM pg_stats")]
    [InlineData("SELECT * FROM pg_stats_ext")]
    [InlineData("SELECT * FROM pg_catalog.pg_stats")]
    public void Check_PlannerStatistics_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedTable);

    [Theory]
    [InlineData("EXPLAIN SELECT * FROM t_1")]
    [InlineData("EXPLAIN ANALYZE SELECT * FROM t_1")]
    public void Check_Explain_Refused(string sql) => Refused(sql, TableSqlRefusal.NotASelect);

    [Theory]
    [InlineData("SELECT reltuples FROM pg_class", nameof(TableSqlRefusal.DisallowedTable))]
    [InlineData("SELECT n_live_tup FROM pg_stat_user_tables", nameof(TableSqlRefusal.DisallowedTable))]
    [InlineData("SELECT pg_relation_size('t_1')", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("SELECT pg_total_relation_size('t_1')", nameof(TableSqlRefusal.DisallowedFunction))]
    public void Check_RowCountSideChannels_Refused(string sql, string reason) => Refused(sql, Enum.Parse<TableSqlRefusal>(reason));

    [Theory]
    [InlineData("SELECT * FROM pg_class", nameof(TableSqlRefusal.DisallowedTable))]
    [InlineData("SELECT * FROM pg_attribute", nameof(TableSqlRefusal.DisallowedTable))]
    [InlineData("SELECT * FROM pg_catalog.pg_class", nameof(TableSqlRefusal.DisallowedTable))]
    [InlineData("SELECT 'pg_authid'::regclass", nameof(TableSqlRefusal.DisallowedType))]
    [InlineData("SELECT CAST('lower' AS regproc)", nameof(TableSqlRefusal.DisallowedType))]
    [InlineData("SELECT * FROM t_1 WHERE c_1 = (SELECT relname FROM pg_class LIMIT 1)", nameof(TableSqlRefusal.DisallowedTable))]
    public void Check_CatalogReads_Refused(string sql, string reason) => Refused(sql, Enum.Parse<TableSqlRefusal>(reason));

    [Fact]
    public void Check_SessionActivity_Refused() =>
        Refused("SELECT query FROM pg_stat_activity", TableSqlRefusal.DisallowedTable);

    [Theory]
    [InlineData("SELECT pg_cancel_backend(1)")]
    [InlineData("SELECT pg_terminate_backend(1)")]
    public void Check_SignalOtherSessions_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedFunction);

    [Theory]
    [InlineData("SELECT pg_advisory_lock(1)", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("SELECT pg_try_advisory_xact_lock(1)", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("SELECT pg_notify('c', 'x')", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("NOTIFY c", nameof(TableSqlRefusal.NotASelect))]
    [InlineData("LISTEN c", nameof(TableSqlRefusal.NotASelect))]
    [InlineData("DECLARE c CURSOR WITH HOLD FOR SELECT * FROM t_1", nameof(TableSqlRefusal.NotASelect))]
    [InlineData("LOCK TABLE t_1", nameof(TableSqlRefusal.NotASelect))]
    public void Check_SideEffectsOutsideTheTransaction_Refused(string sql, string reason) =>
        Refused(sql, Enum.Parse<TableSqlRefusal>(reason));

    [Theory]
    [InlineData("SELECT lo_create(0)")]
    [InlineData("SELECT lo_import('/etc/passwd')")]
    [InlineData("SELECT lo_get(1)")]
    public void Check_LargeObjects_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedFunction);

    [Theory]
    [InlineData("SELECT pg_read_file('/etc/passwd')", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("SELECT \"pg_catalog\".\"pg_read_file\"('/etc/passwd')", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("SELECT PG_CATALOG.PG_SLEEP(10)", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("SELECT pg_ls_dir('.')", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("COPY t_1 TO PROGRAM 'id'", nameof(TableSqlRefusal.NotASelect))]
    [InlineData("COPY (SELECT 1) TO '/tmp/x'", nameof(TableSqlRefusal.NotASelect))]
    public void Check_FilesAndPrograms_Refused(string sql, string reason) => Refused(sql, Enum.Parse<TableSqlRefusal>(reason));

    [Theory]
    [InlineData("SELECT * FROM dblink('host=x', 'SELECT 1')")]
    [InlineData("SELECT dblink_exec('host=x', 'DROP TABLE t')")]
    public void Check_Dblink_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedFunction);

    [Theory]
    [InlineData("SET statement_timeout = 0", nameof(TableSqlRefusal.NotASelect))]
    [InlineData("SET LOCAL work_mem = '4GB'", nameof(TableSqlRefusal.NotASelect))]
    [InlineData("RESET statement_timeout", nameof(TableSqlRefusal.NotASelect))]
    [InlineData("SELECT set_config('statement_timeout', '0', true)", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("SELECT set_config('work_mem', '4GB', true)", nameof(TableSqlRefusal.DisallowedFunction))]
    public void Check_RaisingLimits_Refused(string sql, string reason) => Refused(sql, Enum.Parse<TableSqlRefusal>(reason));

    [Theory]
    [InlineData("SELECT pg_sleep(100)", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("SELECT * FROM generate_series(1, 1000000000)", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("SELECT * FROM ROWS FROM (generate_series(1, 10))", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("SELECT repeat('x', 1000000000)", nameof(TableSqlRefusal.DisallowedFunction))]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r) SELECT * FROM r",
        nameof(TableSqlRefusal.RecursiveWith))]
    public void Check_ResourceExhaustion_Refused(string sql, string reason) => Refused(sql, Enum.Parse<TableSqlRefusal>(reason));

    [Theory]
    [InlineData("WITH d AS (DELETE FROM t_1 RETURNING *) SELECT * FROM d")]
    [InlineData("WITH i AS (INSERT INTO t_1 VALUES (1) RETURNING *) SELECT * FROM i")]
    [InlineData("WITH u AS (UPDATE t_1 SET c_1 = 1 RETURNING *) SELECT * FROM u")]
    public void Check_DataModifyingWith_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedSyntax);

    [Fact]
    public void Check_SelectInto_Refused() =>
        Refused("SELECT * INTO copy FROM t_1", TableSqlRefusal.SelectInto);

    [Theory]
    [InlineData("SELECT * FROM t_1 FOR UPDATE")]
    [InlineData("SELECT * FROM t_1 FOR SHARE")]
    [InlineData("SELECT * FROM t_1 FOR NO KEY UPDATE SKIP LOCKED")]
    [InlineData("SELECT * FROM (SELECT * FROM t_1 FOR UPDATE) s")]
    public void Check_RowLocking_Refused(string sql) => Refused(sql, TableSqlRefusal.RowLocking);

    [Theory]
    [InlineData("SELECT * FROM information_schema.tables")]
    [InlineData("SELECT * FROM information_schema.columns")]
    public void Check_InformationSchema_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedTable);

    [Theory]
    [InlineData("SELECT * FROM tables_other.t_1")]
    [InlineData("SELECT * FROM t_1 JOIN tables_other.t_1 o ON true")]
    [InlineData("SELECT * FROM public.t_1")]
    [InlineData("SELECT * FROM t_9")]
    public void Check_AnotherContainersTables_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedTable);

    [Theory]
    [InlineData("SELECT * FROM (WITH pg_class AS (SELECT 1) SELECT 1) x, pg_class")]
    [InlineData("WITH a AS (SELECT * FROM pg_class) SELECT * FROM a")]
    [InlineData("WITH a AS (SELECT * FROM b), b AS (SELECT 1) SELECT * FROM a")]
    [InlineData("WITH a AS (SELECT * FROM a) SELECT * FROM a")]
    [InlineData("WITH pg_class AS (SELECT 1) SELECT * FROM pg_catalog.pg_class")]
    [InlineData("WITH t_1 AS (SELECT 1) SELECT * FROM tables_other.t_1")]
    public void Check_CteShadowingTricks_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedTable);

    [Theory]
    [InlineData("PREPARE p AS SELECT 1")]
    [InlineData("EXECUTE p")]
    [InlineData("SHOW work_mem")]
    [InlineData("BEGIN")]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    [InlineData("INSERT INTO t_1 VALUES (1)")]
    [InlineData("DELETE FROM t_1")]
    [InlineData("TRUNCATE t_1")]
    [InlineData("VACUUM t_1")]
    public void Check_OtherStatements_Refused(string sql) => Refused(sql, TableSqlRefusal.NotASelect);

    // ---- Text before parsing -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void Check_BlankText_RefusedAsEmpty(string sql) => Refused(sql, TableSqlRefusal.Empty);

    [Theory]
    [InlineData("-- just a comment")]
    [InlineData("/* nothing */")]
    [InlineData(";")]
    public void Check_NoStatement_RefusedAsEmpty(string sql) => Refused(sql, TableSqlRefusal.Empty);

    [Fact]
    public void Check_LongerThanTheLimit_RefusedBeforeParsing()
    {
        string sql = "SELECT 1" + new string(' ', TableSqlCheck.MaxLength);
        Refused(sql, TableSqlRefusal.TooLong);
    }

    [Fact]
    public void Check_ExactlyTheLimit_IsParsed()
    {
        string sql = "SELECT 1" + new string(' ', TableSqlCheck.MaxLength - "SELECT 1".Length);
        sql.Length.Should().Be(TableSqlCheck.MaxLength);
        Allowed(sql);
    }

    [Fact]
    public void Check_NulCharacter_Refused() =>
        Refused("SELECT 1\0; DROP TABLE t_1", TableSqlRefusal.NullCharacter);

    [Theory]
    [InlineData("SELEC 1")]
    [InlineData("SELECT * FROM")]
    [InlineData("SELECT 'unterminated")]
    public void Check_Unparseable_Refused(string sql) => Refused(sql, TableSqlRefusal.Unparseable);

    private static string NestedSubqueries(int depth, string table) =>
        string.Concat(Enumerable.Repeat("SELECT * FROM (", depth)) + $"SELECT * FROM {table}" +
        string.Concat(Enumerable.Repeat(") s", depth));

    [Fact]
    public void Check_NestingPastTheDepthLimit_RefusedTheSameOnEveryPlatform() =>
        // 120 levels parse on Windows and Linux alike, so the refusal comes from the check's own cap.
        Refused(NestedSubqueries(120, "t_1"), TableSqlRefusal.TooDeep);

    [Fact]
    public void Check_NestingPastTheParsersLimit_RefusedWithoutCrashing()
    {
        var act = () => Check(NestedSubqueries(1_000, "t_1"));
        act.Should().NotThrow().Which.Allowed.Should().BeFalse();
        var parens = () => Check("SELECT " + new string('(', 9_000) + "1" + new string(')', 9_000));
        parens.Should().NotThrow();
    }

    [Fact]
    public void Check_DeepSubqueries_InnermostTableDecides()
    {
        Allowed(NestedSubqueries(50, "t_1"));
        Refused(NestedSubqueries(50, "pg_class"), TableSqlRefusal.DisallowedTable);
    }

    // ---- What a plain read may contain -------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT * FROM t_1")]
    [InlineData("SELECT c_1, c_2 FROM tables_abc.t_1 WHERE c_1 > 3 ORDER BY c_2 DESC LIMIT 10 OFFSET 5")]
    [InlineData("TABLE t_1")]
    [InlineData("VALUES (1, 'a'), (2, 'b')")]
    [InlineData("SELECT a.c_1, b.c_2 FROM t_1 a LEFT JOIN t_2 b ON a.c_1 = b.c_1")]
    [InlineData("SELECT * FROM t_1 UNION ALL SELECT * FROM t_2")]
    [InlineData("SELECT * FROM t_1 WHERE c_1 IN (SELECT c_1 FROM t_2)")]
    [InlineData("SELECT * FROM t_1 WHERE EXISTS (SELECT 1 FROM t_2 WHERE t_2.c_1 = t_1.c_1)")]
    [InlineData("SELECT * FROM t_1, LATERAL (SELECT * FROM t_2 WHERE t_2.c_1 = t_1.c_1) l")]
    [InlineData("WITH a AS (SELECT * FROM t_1), b AS (SELECT * FROM a) SELECT * FROM b JOIN a ON true")]
    [InlineData("SELECT * FROM (WITH a AS (SELECT * FROM t_1) SELECT * FROM a) s")]
    [InlineData("SELECT c_1, count(*), sum(c_2), avg(c_2) FROM t_1 GROUP BY c_1 HAVING count(*) > 1")]
    [InlineData("SELECT count(*) FILTER (WHERE c_1 > 0) FROM t_1")]
    [InlineData("SELECT c_1, sum(c_2) FROM t_1 GROUP BY GROUPING SETS ((c_1), ())")]
    [InlineData("SELECT GROUPING(c_1), count(*) FROM t_1 GROUP BY ROLLUP (c_1)")]
    [InlineData("SELECT DISTINCT ON (c_1) c_1, c_2 FROM t_1 ORDER BY c_1, c_2")]
    [InlineData("SELECT row_number() OVER (PARTITION BY c_1 ORDER BY c_2), lag(c_2) OVER w FROM t_1 WINDOW w AS (ORDER BY c_2)")]
    [InlineData("SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY c_2) FROM t_1")]
    [InlineData("SELECT trim(c_1), extract(year FROM c_3), c_3 AT TIME ZONE 'UTC', substring(c_1 from 1 for 2), position('a' in c_1) FROM t_1")]
    [InlineData("SELECT c_1::integer, CAST(c_2 AS numeric(10, 2)), c_3::timestamp with time zone, '1 day'::interval FROM t_1")]
    [InlineData("SELECT c_1::pg_catalog.int8, pg_catalog.lower(c_2) FROM t_1")]
    [InlineData("SELECT CURRENT_DATE, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP(3), LOCALTIME, LOCALTIMESTAMP, CURRENT_TIME, now()")]
    [InlineData("SELECT CASE WHEN c_1 IS NULL THEN 'none' ELSE upper(c_2) END FROM t_1")]
    [InlineData("SELECT coalesce(c_1, 0), nullif(c_1, 0), greatest(c_1, c_2), least(c_1, c_2) FROM t_1")]
    [InlineData("SELECT * FROM t_1 WHERE c_1 IS NOT NULL AND c_2 IS TRUE OR NOT (c_1 BETWEEN 1 AND 5)")]
    [InlineData("SELECT * FROM t_1 WHERE c_1 LIKE 'a%' AND c_2 ILIKE '%b' AND c_1 ~ 'x' AND c_1 IS DISTINCT FROM c_2")]
    [InlineData("SELECT * FROM t_1 WHERE c_1 = ANY (ARRAY[1, 2, 3])")]
    [InlineData("SELECT (ARRAY[1, 2])[1], (ROW(1, 2)), ARRAY(SELECT c_1 FROM t_2) FROM t_1")]
    [InlineData("SELECT c_1 COLLATE \"C\" FROM t_1 ORDER BY 1 NULLS LAST")]
    [InlineData("SELECT 1 OPERATOR(pg_catalog.+) 2, 1.5, true, B'101', NULL")]
    public void Check_PlainReads_Allowed(string sql) => Allowed(sql);

    [Fact]
    public void Check_PgNamedTableInScope_StillRefused() =>
        // Container tables are named t_<id>, so a pg_ name in scope is a mistake that could reach the catalog.
        Refused("SELECT * FROM pg_x", TableSqlRefusal.DisallowedTable);

    // ---- Branches of each rule ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT $1")]
    [InlineData("SELECT * FROM t_1 TABLESAMPLE SYSTEM (10)")]
    [InlineData("SELECT * FROM XMLTABLE('/a' PASSING '<a/>' COLUMNS x int)")]
    [InlineData("SELECT json_value('{}', '$.a')")]
    [InlineData("SELECT c_1 IS JSON FROM t_1")]
    [InlineData("SELECT xmlelement(name a)")]
    public void Check_SyntaxOffTheAllowlist_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedSyntax);

    [Theory]
    [InlineData("SELECT public.lower('a')")]
    [InlineData("SELECT tables_abc.lower('a')")]
    [InlineData("SELECT db.pg_catalog.lower('a')")]
    [InlineData("SELECT format('%s', 1)")]
    [InlineData("SELECT * FROM t_1 ORDER BY random()")]
    public void Check_FunctionOffTheAllowlist_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedFunction);

    [Theory]
    [InlineData("SELECT 1 OPERATOR(public.+) 2")]
    [InlineData("SELECT 1 OPERATOR(tables_abc.===) 2")]
    public void Check_OperatorOutsidePgCatalog_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedOperator);

    [Theory]
    [InlineData("SELECT 1::public.int4")]
    [InlineData("SELECT 'x'::regtype")]
    [InlineData("SELECT 'x'::oid")]
    [InlineData("SELECT '{}'::jsonb")]
    [InlineData("SELECT 1::db.pg_catalog.int4")]
    public void Check_TypeOffTheAllowlist_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedType);

    [Theory]
    [InlineData("SELECT * FROM db.tables_abc.t_1")]
    [InlineData("SELECT * FROM \"T_1\"")]
    [InlineData("SELECT * FROM t_1 JOIN pg_x ON true")]
    public void Check_TableOffTheScope_Refused(string sql) => Refused(sql, TableSqlRefusal.DisallowedTable);

    [Fact]
    public void Check_AnotherScope_UsesItsOwnSchemaAndTables()
    {
        var other = new TableSqlScope("tables_other", new HashSet<string> { "t_9" });
        TableSqlCheck.Check("SELECT * FROM tables_other.t_9", other).Allowed.Should().BeTrue();
        TableSqlCheck.Check("SELECT * FROM tables_abc.t_1", other).Refusal.Should().Be(TableSqlRefusal.DisallowedTable);
    }

    [Fact]
    public void Allow_IsAllowedWithNoDetail()
    {
        TableSqlVerdict.Allow.Allowed.Should().BeTrue();
        TableSqlVerdict.Allow.Detail.Should().BeEmpty();
    }

    // ---- Random input and speed --------------------------------------------------------------------------------

    private static readonly string[] Disallowed =
    [
        "pg_sleep(1)", "set_config('a', 'b', false)", "current_setting('a')", "pg_read_file('x')",
        "pg_catalog.pg_sleep(1)", "\"pg_catalog\".\"pg_sleep\"(1)", "PG_SLEEP(1)", "pg_cancel_backend(1)",
        "lo_import('x')", "dblink('a', 'b')", "'x'::regclass", "current_user", "$1", "(SELECT 1 FROM pg_class)",
        "(SELECT 1 FROM tables_other.t_1)", "(SELECT 1 FROM information_schema.tables)", "generate_series(1, 2)",
    ];

    private static readonly string[] Templates =
    [
        "SELECT {0}", "SELECT c_1, {0} FROM t_1", "SELECT * FROM t_1 WHERE c_1 = {0}",
        "SELECT * FROM t_1 ORDER BY {0}", "SELECT count(*) FROM t_1 GROUP BY c_1 HAVING max(c_2) > {0}",
        "WITH a AS (SELECT {0} AS x) SELECT * FROM a", "SELECT * FROM (SELECT {0} AS x) s",
        "SELECT * FROM t_1 UNION SELECT {0}, 1", "SELECT CASE WHEN true THEN {0} END",
        "SELECT coalesce({0}, 1)", "SELECT ARRAY[{0}]", "SELECT * FROM t_1 JOIN t_2 ON {0} = 1",
    ];

    [Fact]
    [Trait("Size", "Medium")]
    public void Check_RandomText_NeverThrows()
    {
        var pieces = Gen.OneOfConst("SELECT", " ", "*", "FROM", "t_1", "pg_class", "(", ")", ";", "'", "\"", "$$", "--",
            "/*", "*/", ",", "1", "::", "regclass", "WITH", "AS", "set_config", "\n", "\0", "\\", "E'", "U&'");
        Gen.Frequency((1, Gen.String), (3, pieces.Array[0, 30].Select(p => string.Concat(p))))
            .Sample(sql =>
            {
                var verdict = TableSqlCheck.Check(sql, Scope);
                verdict.Should().NotBeNull();
                if (!verdict.Allowed)
                    verdict.Detail.Should().NotBeEmpty();
            });
    }

    [Fact]
    [Trait("Size", "Medium")]
    public void Check_DisallowedExpressionInAnyPlainRead_Refused()
    {
        Gen.Select(Gen.OneOfConst(Templates), Gen.OneOfConst(Disallowed)).Sample((template, bad) =>
        {
            string sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, bad);
            TableSqlCheck.Check(sql, Scope).Allowed.Should().BeFalse(sql);
        });
    }

    [Fact]
    public void Check_TemplatesWithAnAllowedExpression_Allowed()
    {
        // The other half of the test above: the templates themselves are plain reads, so the refusal comes from the splice.
        foreach (string template in Templates)
            Allowed(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, "lower('a')"));
    }

    [Fact]
    [Trait("Size", "Medium")]
    public void Check_TypicalQuery_P95UnderFiveMilliseconds()
    {
        const string sql = "WITH recent AS (SELECT c_1, c_2, c_3 FROM t_1 WHERE c_3 > CURRENT_DATE - 30) " +
            "SELECT r.c_1, count(*), avg(r.c_2), max(t.c_2), row_number() OVER (ORDER BY count(*) DESC) " +
            "FROM recent r LEFT JOIN t_2 t ON t.c_1 = r.c_1 WHERE lower(r.c_1) LIKE 'a%' " +
            "GROUP BY r.c_1 HAVING count(*) > 1 ORDER BY 2 DESC LIMIT 50";
        for (int i = 0; i < 50; i++)
            Check(sql).Allowed.Should().BeTrue();

        var times = new double[500];
        for (int i = 0; i < times.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            Check(sql);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(times);
        times[(int)(times.Length * 0.95)].Should().BeLessThan(5);
    }
}
