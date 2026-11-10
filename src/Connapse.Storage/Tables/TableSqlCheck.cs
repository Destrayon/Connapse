using Google.Protobuf;
using Google.Protobuf.Reflection;
using Pg = PgSqlParser;

namespace Connapse.Storage.Tables;

/// <summary>Why <see cref="TableSqlCheck"/> refused a query, or <see cref="None"/> when it allowed it.</summary>
internal enum TableSqlRefusal
{
    None,
    Empty,
    TooLong,
    NullCharacter,
    TooDeep,
    Unparseable,
    MultipleStatements,
    NotASelect,
    SelectInto,
    RowLocking,
    RecursiveWith,
    DisallowedSyntax,
    DisallowedTable,
    DisallowedFunction,
    DisallowedOperator,
    DisallowedType,
}

/// <summary>The outcome of <see cref="TableSqlCheck.Check"/>: allowed, or refused with a reason the agent can read.</summary>
internal sealed record TableSqlVerdict(TableSqlRefusal Refusal, string Detail)
{
    internal static readonly TableSqlVerdict Allow = new(TableSqlRefusal.None, "");

    internal bool Allowed => Refusal == TableSqlRefusal.None;
}

/// <summary>
/// The tables an agent's query may read: the container's schema, and the table names in it the caller may query.
/// Names compare exactly, as PostgreSQL stores them after its parser folds unquoted identifiers to lower case.
/// </summary>
internal sealed record TableSqlScope(string Schema, IReadOnlySet<string> Tables);

/// <summary>
/// Checks an agent-written query before it runs (CON-325, ticket b of docs/plans/con-320-raw-sql-rls.md). It parses the
/// text with libpg_query, PostgreSQL's own parser (through <c>pgsqlparser</c>), and allows it only when it is exactly
/// one plain <c>SELECT</c> that reads the scope's tables and calls allowlisted functions, operators and types. Every
/// node kind the parse tree can hold is refused unless <see cref="AllowedNodes"/> names it, so syntax PostgreSQL adds
/// later is refused until someone reviews it. This is one layer of four (docs/research/rls-raw-sql-agents-2026-11-10.md
/// section 2): it stops what grants can't, such as turning read-only off, raising limits, forging the request token or
/// cancelling other sessions, while grants, row-level security and the runner's limits still stand behind it.
/// The runner that sends an allowed query must (a) send this exact text as one command, (b) keep
/// <c>standard_conforming_strings</c> on, as libpg_query assumes, and (c) pin <c>search_path</c> to the container's
/// schema followed by <c>pg_catalog, pg_temp</c>, so an unqualified name resolves to the container before the catalog.
/// </summary>
internal static class TableSqlCheck
{
    /// <summary>The longest query text checked; anything longer is refused before it is parsed.</summary>
    internal const int MaxLength = 20_000;

    /// <summary>
    /// The deepest parse tree checked, counted in protobuf messages (a nested subquery adds four). The parser's own
    /// limit depends on the platform's stack, so without this cap the same query could pass on Linux and fail to parse
    /// on Windows.
    /// </summary>
    internal const int MaxDepth = 400;

    private const string Catalog = "pg_catalog";

    /// <summary>The parse tree messages a plain read may contain. Everything else is refused.</summary>
    private static readonly HashSet<string> AllowedNodes = new(StringComparer.Ordinal)
    {
        "ParseResult", "RawStmt", "Node", "List", "SelectStmt", "ResTarget", "ColumnRef", "A_Star", "A_Const",
        "String", "Integer", "Float", "Boolean", "BitString", "A_Expr", "BoolExpr", "NullTest", "BooleanTest",
        "CaseExpr", "CaseWhen", "CoalesceExpr", "MinMaxExpr", "FuncCall", "TypeCast", "TypeName", "SubLink",
        "RangeVar", "RangeSubselect", "RangeFunction", "JoinExpr", "Alias", "SortBy", "WindowDef", "WithClause",
        "CommonTableExpr", "A_ArrayExpr", "A_Indirection", "A_Indices", "RowExpr", "GroupingSet", "GroupingFunc",
        "CollateClause", "SQLValueFunction",
    };

    /// <summary>
    /// Built-in aggregate, window, math, string and date functions, by the name the parser gives them (SQL syntax such
    /// as <c>TRIM</c>, <c>EXTRACT</c> or <c>AT TIME ZONE</c> arrives as <c>pg_catalog.btrim</c>, <c>extract</c>,
    /// <c>timezone</c>). Nothing that reads or changes settings, sleeps, locks, signals, reads files or the catalog, or
    /// generates rows; and nothing that builds an arbitrarily long value from a short one (<c>repeat</c>, <c>lpad</c>).
    /// </summary>
    private static readonly HashSet<string> AllowedFunctions = new(StringComparer.Ordinal)
    {
        // aggregates
        "count", "sum", "avg", "min", "max", "bool_and", "bool_or", "every", "string_agg", "array_agg", "stddev",
        "stddev_pop", "stddev_samp", "variance", "var_pop", "var_samp", "percentile_cont", "percentile_disc", "mode",
        "corr", "covar_pop", "covar_samp", "regr_slope", "regr_intercept", "regr_r2", "regr_count",
        // window
        "row_number", "rank", "dense_rank", "percent_rank", "cume_dist", "ntile", "lag", "lead", "first_value",
        "last_value", "nth_value",
        // math
        "abs", "ceil", "ceiling", "floor", "round", "trunc", "sign", "sqrt", "cbrt", "power", "exp", "ln", "log",
        "log10", "mod", "div", "width_bucket", "pi",
        // string
        "lower", "upper", "length", "char_length", "character_length", "octet_length", "substring", "substr", "btrim",
        "ltrim", "rtrim", "replace", "concat", "concat_ws", "left", "right", "position", "strpos", "split_part",
        "starts_with", "initcap", "reverse", "to_char", "to_number", "to_date", "to_timestamp",
        // date and time
        "date_trunc", "date_part", "extract", "age", "now", "make_date", "make_time", "make_timestamp",
        "make_interval", "date_bin", "justify_days", "justify_hours", "justify_interval", "timezone", "isfinite",
        "overlaps",
    };

    /// <summary>Types a value may be cast to. No <c>reg*</c> type, since those look names up in the catalog.</summary>
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "int2", "int4", "int8", "float4", "float8", "numeric", "text", "varchar", "bpchar", "bool", "date", "time",
        "timetz", "timestamp", "timestamptz", "interval",
    };

    /// <summary>Date and time keywords; <c>CURRENT_USER</c>, <c>SESSION_USER</c> and the like stay out.</summary>
    private static readonly HashSet<Pg.SQLValueFunctionOp> AllowedValueFunctions =
    [
        Pg.SQLValueFunctionOp.SvfopCurrentDate, Pg.SQLValueFunctionOp.SvfopCurrentTime,
        Pg.SQLValueFunctionOp.SvfopCurrentTimeN, Pg.SQLValueFunctionOp.SvfopCurrentTimestamp,
        Pg.SQLValueFunctionOp.SvfopCurrentTimestampN, Pg.SQLValueFunctionOp.SvfopLocaltime,
        Pg.SQLValueFunctionOp.SvfopLocaltimeN, Pg.SQLValueFunctionOp.SvfopLocaltimestamp,
        Pg.SQLValueFunctionOp.SvfopLocaltimestampN,
    ];

    /// <summary>Checks <paramref name="sql"/> against <paramref name="scope"/>. Never throws for any query text.</summary>
    internal static TableSqlVerdict Check(string sql, TableSqlScope scope)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return Refuse(TableSqlRefusal.Empty, "the query is empty");
        if (sql.Length > MaxLength)
            return Refuse(TableSqlRefusal.TooLong, $"the query is longer than {MaxLength} characters");
        // libpg_query reads a C string, so it would stop at a NUL the server might not.
        if (sql.Contains('\0', StringComparison.Ordinal))
            return Refuse(TableSqlRefusal.NullCharacter, "the query contains a NUL character");

        var parsed = Pg.Parser.Parse(sql);
        if (!parsed.IsSuccess)
            return Refuse(TableSqlRefusal.Unparseable, $"the query doesn't parse: {parsed.Error.Message}");

        var statements = parsed.Value.Stmts;
        if (statements.Count == 0)
            return Refuse(TableSqlRefusal.Empty, "the query has no statement");
        if (statements.Count > 1)
            return Refuse(TableSqlRefusal.MultipleStatements, "send one statement at a time");
        if (statements[0].Stmt.NodeCase != Pg.Node.NodeOneofCase.SelectStmt)
            return Refuse(TableSqlRefusal.NotASelect, $"only SELECT is allowed, not {statements[0].Stmt.NodeCase}");

        return Walk(statements[0].Stmt.SelectStmt, scope);
    }

    /// <summary>
    /// Visits every message in the tree once, without recursion, so a deeply nested query can't overflow the stack.
    /// Each item carries the common table expression names visible at that point.
    /// </summary>
    private static TableSqlVerdict Walk(Pg.SelectStmt root, TableSqlScope scope)
    {
        var pending = new Stack<(IMessage Message, CteNames? Ctes, int Depth)>();
        pending.Push((root, null, 1));
        while (pending.TryPop(out var item))
        {
            var (message, ctes, depth) = item;
            if (depth > MaxDepth)
                return Refuse(TableSqlRefusal.TooDeep, $"the query nests deeper than {MaxDepth} levels");
            if (!AllowedNodes.Contains(message.Descriptor.Name))
                return Refuse(TableSqlRefusal.DisallowedSyntax, $"{message.Descriptor.Name} is not allowed");
            if (Inspect(message, scope, ctes) is { } refused)
                return refused;

            switch (message)
            {
                case Pg.SelectStmt select when select.WithClause is { } with:
                    var all = ctes;
                    foreach (var cte in with.Ctes)
                    {
                        // A plain WITH's query sees the names before it, never its own or later ones.
                        pending.Push((cte, all, depth + 1));
                        all = new CteNames(cte.CommonTableExpr.Ctename, all);
                    }
                    PushChildren(pending, select, all, depth + 1, skip: Pg.SelectStmt.WithClauseFieldNumber);
                    break;
                default:
                    PushChildren(pending, message, ctes, depth + 1, skip: -1);
                    break;
            }
        }

        return TableSqlVerdict.Allow;
    }

    private static void PushChildren(
        Stack<(IMessage, CteNames?, int)> pending, IMessage message, CteNames? ctes, int depth, int skip)
    {
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            if (field.FieldType != FieldType.Message || field.FieldNumber == skip)
                continue;
            object? value = field.Accessor.GetValue(message);
            if (field.IsRepeated)
            {
                foreach (object? child in (System.Collections.IList)value!)
                    pending.Push(((IMessage)child!, ctes, depth));
            }
            else if (value is IMessage child)
            {
                pending.Push((child, ctes, depth));
            }
        }
    }

    /// <summary>The rules for one message beyond its kind being allowed. Null when it passes.</summary>
    private static TableSqlVerdict? Inspect(IMessage message, TableSqlScope scope, CteNames? ctes) => message switch
    {
        Pg.SelectStmt { IntoClause: not null } =>
            Refuse(TableSqlRefusal.SelectInto, "SELECT INTO creates a table"),
        Pg.SelectStmt select when select.LockingClause.Count > 0 =>
            Refuse(TableSqlRefusal.RowLocking, "FOR UPDATE and FOR SHARE lock rows"),
        Pg.SelectStmt { WithClause.Recursive: true } =>
            Refuse(TableSqlRefusal.RecursiveWith, "WITH RECURSIVE is not allowed"),
        Pg.RangeVar table => CheckTable(table, scope, ctes),
        Pg.FuncCall call => CheckFunction(call),
        Pg.A_Expr expression => CheckOperator(expression),
        Pg.TypeName type => CheckType(type),
        Pg.SQLValueFunction value when !AllowedValueFunctions.Contains(value.Op) =>
            Refuse(TableSqlRefusal.DisallowedFunction, $"{value.Op} is not allowed"),
        _ => null,
    };

    private static TableSqlVerdict? CheckTable(Pg.RangeVar table, TableSqlScope scope, CteNames? ctes)
    {
        string name = table.Relname;
        bool qualified = table.Schemaname.Length > 0 || table.Catalogname.Length > 0;
        if (!qualified && CteNames.Contains(ctes, name))
            return null;
        if (table.Catalogname.Length > 0 || (qualified && table.Schemaname != scope.Schema))
            return Refuse(TableSqlRefusal.DisallowedTable, $"{Qualified(table)} is outside this container");
        // pg_catalog is searched before the container unless the runner lists it later; refusing pg_ names here
        // means a mistake in search_path can't turn a container name into a catalog table.
        if (name.StartsWith("pg_", StringComparison.Ordinal) || !scope.Tables.Contains(name))
            return Refuse(TableSqlRefusal.DisallowedTable, $"{Qualified(table)} is not a table in this container");
        return null;
    }

    private static string Qualified(Pg.RangeVar table) =>
        string.Join('.', new[] { table.Catalogname, table.Schemaname, table.Relname }.Where(p => p.Length > 0));

    private static TableSqlVerdict? CheckFunction(Pg.FuncCall call) =>
        BuiltIn(call.Funcname, AllowedFunctions) is not null
            ? null
            : Refuse(TableSqlRefusal.DisallowedFunction, $"function {Names(call.Funcname)} is not allowed");

    // Operators are built-in only: the reader can't create one. A schema-qualified OPERATOR() must name pg_catalog.
    private static TableSqlVerdict? CheckOperator(Pg.A_Expr expression) =>
        expression.Name.Count switch
        {
            <= 1 => null,
            2 when expression.Name[0].String?.Sval == Catalog => null,
            _ => Refuse(TableSqlRefusal.DisallowedOperator, $"operator {Names(expression.Name)} is not allowed"),
        };

    private static TableSqlVerdict? CheckType(Pg.TypeName type) =>
        BuiltIn(type.Names, AllowedTypes) is not null
            ? null
            : Refuse(TableSqlRefusal.DisallowedType, $"type {Names(type.Names)} is not allowed");

    /// <summary>The bare name when <paramref name="names"/> is <c>name</c> or <c>pg_catalog.name</c> and allowed.</summary>
    private static string? BuiltIn(IList<Pg.Node> names, HashSet<string> allowed)
    {
        string? last = names.Count is 1 or 2 ? names[^1].String?.Sval : null;
        bool catalog = names.Count == 1 || names[0].String?.Sval == Catalog;
        return last is not null && catalog && allowed.Contains(last) ? last : null;
    }

    private static string Names(IEnumerable<Pg.Node> names) =>
        string.Join('.', names.Select(n => n.String?.Sval ?? n.NodeCase.ToString()));

    private static TableSqlVerdict Refuse(TableSqlRefusal refusal, string detail) => new(refusal, detail);

    /// <summary>The common table expression names in scope, innermost first.</summary>
    private sealed record CteNames(string Name, CteNames? Outer)
    {
        internal static bool Contains(CteNames? names, string name)
        {
            for (var n = names; n is not null; n = n.Outer)
            {
                if (n.Name == name)
                    return true;
            }
            return false;
        }
    }
}
