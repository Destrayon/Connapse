using System.Text.RegularExpressions;
using Connapse.Core;

namespace Connapse.Eval.Runs;

/// <summary>
/// One query of the CON-311 eval's admin library: the definition as an admin would save it, with <c>@&lt;table&gt;</c>
/// where a table id goes, the tables-v1 templates it answers, and a tune question with the arguments that answer it, so
/// the library can be checked before any held-out run.
/// </summary>
public sealed record LibraryQuery(SavedQueryDefinition Definition, IReadOnlyList<string> Templates, string TuneId, string TuneArguments);

/// <summary>
/// The admin library for the CON-311 proof (<c>eval/pass-bars.md</c>, Admin-saved queries). It was written from the
/// table headers and the tune questions only, before any held-out question was read or run, and is frozen by the commit
/// that adds the bar. Ticket questions are left out because a saved query reads one table and tickets are three, and
/// seen-as-of questions because a parameter can't set <c>seen_as_of</c>.
/// </summary>
public static partial class TablesSavedLibrary
{
    private static SavedQueryParameter P(string name, SavedQueryParameterType type, string description, string? fallback = null) =>
        new(name, type, description, fallback);

    private const SavedQueryParameterType Text = SavedQueryParameterType.Text;
    private const SavedQueryParameterType Whole = SavedQueryParameterType.WholeNumber;
    private const SavedQueryParameterType Number = SavedQueryParameterType.Number;
    private const SavedQueryParameterType Flag = SavedQueryParameterType.Boolean;
    private const SavedQueryParameterType Day = SavedQueryParameterType.Date;

    private static SavedQueryParameter From(string what) => P("from", Day, $"First day of the {what} range, inclusive, as YYYY-MM-DD; for a month use its first day");

    private static SavedQueryParameter To(string what) => P("to", Day, $"Last day of the {what} range, inclusive, as YYYY-MM-DD; for a month use its last day");

    private static LibraryQuery Read(string name, string description, string request, SavedQueryParameter[] parameters,
        string[] templates, string tuneId, string tuneArguments) =>
        new(new SavedQueryDefinition(name, description, SavedQueryKind.Read, request, parameters), templates, tuneId, tuneArguments);

    private static LibraryQuery Aggregate(string name, string description, string request, SavedQueryParameter[] parameters,
        string[] templates, string tuneId, string tuneArguments) =>
        new(new SavedQueryDefinition(name, description, SavedQueryKind.Aggregate, request, parameters), templates, tuneId, tuneArguments);

    public static readonly IReadOnlyList<LibraryQuery> Queries =
    [
        // Lookups.
        Read("order_by_id", "One order by its order ID: customer, product, date, quantity, price, discount, total, status and channel.",
            """{"table": "@orders", "where": {"column": "order_id", "op": "=", "value": {"param": "order_id"}}}""",
            [P("order_id", Whole, "The order ID, such as 561047")], ["order_total"], "tune-002", """{"order_id": 561047}"""),
        Read("bank_customer_by_id", "One bank customer by ID: age, gender, education, income category, credit limit, balances and transactions.",
            """{"table": "@bank", "where": {"column": "id", "op": "=", "value": {"param": "id"}}}""",
            [P("id", Whole, "The bank customer ID")], ["bank_limit"], "tune-001", """{"id": 5159}"""),
        Read("billionaire_by_name", "One person in the Forbes billionaires list by full name: rank, age, final worth in millions of dollars, category, country and more.",
            """{"table": "@forbes", "where": {"column": "personName", "op": "=", "value": {"param": "person"}}}""",
            [P("person", Text, "The person's full name as listed, such as Rakesh Gangwal")], ["forbes_worth"], "tune-004", """{"person": "Rakesh Gangwal"}"""),
        Read("supermarket_invoice", "One supermarket purchase by invoice ID: branch, city, customer type, gender, product line, price, quantity, total, date, payment and rating.",
            """{"table": "@supermarket", "where": {"column": "Invoice ID", "op": "=", "value": {"param": "invoice_id"}}}""",
            [P("invoice_id", Text, "The invoice ID, such as 349-97-8902")], ["market_payment"], "tune-006", """{"invoice_id": "349-97-8902"}"""),
        Read("invoice_by_id", "One customer invoice by invoice ID: customer, issue and due dates, amount, paid date, status and currency.",
            """{"table": "@invoices", "where": {"column": "invoice_id", "op": "=", "value": {"param": "invoice_id"}}}""",
            [P("invoice_id", Text, "The invoice ID, such as INV-000427")], ["invoice_due"], "tune-007", """{"invoice_id": "INV-000427"}"""),

        // Filtered counts.
        Aggregate("supermarket_purchase_count", "How many supermarket purchases of one product line in one city were paid with one payment method.",
            """{"table": "@supermarket", "aggregates": [{"aggregate": "count"}], "where": {"and": [{"column": "Product line", "op": "=", "value": {"param": "product_line"}}, {"column": "City", "op": "=", "value": {"param": "city"}}, {"column": "Payment", "op": "=", "value": {"param": "payment"}}]}}""",
            [P("product_line", Text, "Product line, such as Food and beverages"), P("city", Text, "City: Yangon, Mandalay or Naypyitaw"), P("payment", Text, "Payment method: Ewallet, Cash or Credit card")],
            ["market_count"], "tune-011", """{"product_line": "Food and beverages", "city": "Naypyitaw", "payment": "Ewallet"}"""),
        Aggregate("billionaire_count", "How many people in the Forbes billionaires list are in one category and from one country.",
            """{"table": "@forbes", "aggregates": [{"aggregate": "count"}], "where": {"and": [{"column": "category", "op": "=", "value": {"param": "category"}}, {"column": "country", "op": "=", "value": {"param": "country"}}]}}""",
            [P("category", Text, "Forbes category, such as Food & Beverage"), P("country", Text, "Country, such as Hong Kong")],
            ["forbes_count"], "tune-012", """{"category": "Food & Beverage", "country": "Hong Kong"}"""),
        Aggregate("employee_count", "How many employees of one department work at one office, counting only active employees unless told otherwise.",
            """{"table": "@employees", "aggregates": [{"aggregate": "count"}], "where": {"and": [{"column": "department", "op": "=", "value": {"param": "department"}}, {"column": "office", "op": "=", "value": {"param": "office"}}, {"column": "active", "op": "=", "value": {"param": "active"}}]}}""",
            [P("department", Text, "Department, such as Finance"), P("office", Text, "Office city, such as Sao Paulo"), P("active", Flag, "true for active employees, false for former ones", "true")],
            ["employees_active"], "tune-013", """{"department": "Finance", "office": "Sao Paulo"}"""),
        Aggregate("bank_customer_count", "How many bank customers in one income category are within an age range.",
            """{"table": "@bank", "aggregates": [{"aggregate": "count"}], "where": {"and": [{"column": "income_category", "op": "=", "value": {"param": "income_category"}}, {"column": "customer_age", "op": "between", "values": [{"param": "min_age"}, {"param": "max_age"}]}]}}""",
            [P("income_category", Text, "Income category exactly as stored, such as $60K - $80K"), P("min_age", Whole, "Youngest age, inclusive"), P("max_age", Whole, "Oldest age, inclusive")],
            ["bank_income"], "tune-016", """{"income_category": "$60K - $80K", "min_age": 30, "max_age": 34}"""),
        Aggregate("product_count", "How many products of one category come from one supplier, counting only products still sold unless told otherwise.",
            """{"table": "@products", "aggregates": [{"aggregate": "count"}], "where": {"and": [{"column": "supplier", "op": "=", "value": {"param": "supplier"}}, {"column": "category", "op": "=", "value": {"param": "category"}}, {"column": "discontinued", "op": "=", "value": {"param": "discontinued"}}]}}""",
            [P("supplier", Text, "Supplier, such as Wingtip Distribution"), P("category", Text, "Product category, such as Services"), P("discontinued", Flag, "false for products still sold, true for discontinued ones", "false")],
            ["products_discontinued"], "tune-017", """{"supplier": "Wingtip Distribution", "category": "Services"}"""),

        // Sums and averages.
        Aggregate("nba_team_points", "Total points scored by one team's players in one NBA season and season type.",
            """{"table": "@nba", "aggregates": [{"aggregate": "sum", "column": "PTS"}], "where": {"and": [{"column": "TEAM", "op": "=", "value": {"param": "team"}}, {"column": "year", "op": "=", "value": {"param": "season"}}, {"column": "Season_type", "op": "=", "value": {"param": "season_type"}}]}}""",
            [P("team", Text, "Team abbreviation, such as LAC"), P("season", Text, "Season, such as 2015-16"), P("season_type", Text, "Regular Season or Playoffs", "\"Regular Season\"")],
            ["nba_team_points"], "tune-021", """{"team": "LAC", "season": "2015-16"}"""),
        Aggregate("warehouse_units_on_hand", "Total units on hand, in the current inventory, across one warehouse's SKUs with one reorder point that were last counted in a date range.",
            """{"table": "@inventory", "aggregates": [{"aggregate": "sum", "column": "on_hand"}], "where": {"and": [{"column": "warehouse", "op": "=", "value": {"param": "warehouse"}}, {"column": "reorder_point", "op": "=", "value": {"param": "reorder_point"}}, {"column": "last_counted", "op": "between", "values": [{"param": "from"}, {"param": "to"}]}]}}""",
            [P("warehouse", Text, "Warehouse city, such as Toronto"), P("reorder_point", Whole, "The SKUs' reorder point"), From("last-counted"), To("last-counted")],
            ["warehouse_stock"], "tune-022", """{"warehouse": "Toronto", "reorder_point": 50, "from": "2025-10-01", "to": "2025-10-31"}"""),
        Aggregate("order_average_quantity", "Average quantity per order for orders with one discount percentage placed in a date range.",
            """{"table": "@orders", "aggregates": [{"aggregate": "avg", "column": "quantity"}], "where": {"and": [{"column": "discount_pct", "op": "=", "value": {"param": "discount_pct"}}, {"column": "order_date", "op": "between", "values": [{"param": "from"}, {"param": "to"}]}]}}""",
            [P("discount_pct", Number, "Discount percentage, such as 5 for 5%"), From("order-date"), To("order-date")],
            ["order_quantity"], "tune-024", """{"discount_pct": 5, "from": "2025-05-01", "to": "2025-05-31"}"""),
        Aggregate("supermarket_gross_income", "Total gross income from one product line at one supermarket city in a date range.",
            """{"table": "@supermarket", "aggregates": [{"aggregate": "sum", "column": "gross income"}], "where": {"and": [{"column": "City", "op": "=", "value": {"param": "city"}}, {"column": "Product line", "op": "=", "value": {"param": "product_line"}}, {"column": "Date", "op": "between", "values": [{"param": "from"}, {"param": "to"}]}]}}""",
            [P("city", Text, "City: Yangon, Mandalay or Naypyitaw"), P("product_line", Text, "Product line, such as Home and lifestyle"), From("purchase-date"), To("purchase-date")],
            ["market_income"], "tune-025", """{"city": "Mandalay", "product_line": "Home and lifestyle", "from": "2019-02-01", "to": "2019-02-28"}"""),
        Aggregate("billionaire_average_age", "Average age of the Forbes billionaires from one country, self-made or not.",
            """{"table": "@forbes", "aggregates": [{"aggregate": "avg", "column": "age"}], "where": {"and": [{"column": "country", "op": "=", "value": {"param": "country"}}, {"column": "selfMade", "op": "=", "value": {"param": "self_made"}}]}}""",
            [P("country", Text, "Country, such as France"), P("self_made", Flag, "true for self-made billionaires, false for those who inherited")],
            ["forbes_age"], "tune-027", """{"country": "France", "self_made": true}"""),
        Aggregate("bank_average_credit_limit", "Average credit limit of bank customers with one education level in one income category.",
            """{"table": "@bank", "aggregates": [{"aggregate": "avg", "column": "credit_limit"}], "where": {"and": [{"column": "income_category", "op": "=", "value": {"param": "income_category"}}, {"column": "education_level", "op": "=", "value": {"param": "education_level"}}]}}""",
            [P("income_category", Text, "Income category exactly as stored, such as $60K - $80K"), P("education_level", Text, "Education level, such as High School")],
            ["bank_credit"], "tune-030", """{"income_category": "$60K - $80K", "education_level": "High School"}"""),

        // Top groups.
        Aggregate("top_customers_by_spend", "Customer IDs ranked by how much they spent on orders of one status placed in a date range, highest first (top 10).",
            """{"table": "@orders", "aggregates": [{"aggregate": "sum", "column": "total"}], "group_by": ["customer_id"], "where": {"and": [{"column": "status", "op": "=", "value": {"param": "status"}}, {"column": "order_date", "op": "between", "values": [{"param": "from"}, {"param": "to"}]}]}, "order_by": [{"aggregate": "sum", "column": "total", "direction": "desc"}, {"column": "customer_id", "direction": "asc"}], "limit": 10}""",
            [P("status", Text, "Order status, such as completed", "\"completed\""), From("order-date"), To("order-date")],
            ["top_customers"], "tune-032", """{"from": "2023-09-01", "to": "2023-09-30"}"""),
        Aggregate("top_billionaire_countries", "Countries ranked by how many Forbes billionaires they have in one category, most first (top 10).",
            """{"table": "@forbes", "aggregates": [{"aggregate": "count"}], "group_by": ["country"], "where": {"column": "category", "op": "=", "value": {"param": "category"}}, "order_by": [{"aggregate": "count", "direction": "desc"}, {"column": "country", "direction": "asc"}], "limit": 10}""",
            [P("category", Text, "Forbes category, such as Media & Entertainment")],
            ["forbes_countries"], "tune-035", """{"category": "Media & Entertainment"}"""),
        Aggregate("supermarket_sales_by_product_line", "Supermarket product lines ranked by total sales to one gender at one city in a date range, highest first.",
            """{"table": "@supermarket", "aggregates": [{"aggregate": "sum", "column": "Total"}], "group_by": ["Product line"], "where": {"and": [{"column": "City", "op": "=", "value": {"param": "city"}}, {"column": "Gender", "op": "=", "value": {"param": "gender"}}, {"column": "Date", "op": "between", "values": [{"param": "from"}, {"param": "to"}]}]}, "order_by": [{"aggregate": "sum", "column": "Total", "direction": "desc"}, {"column": "Product line", "direction": "asc"}], "limit": 10}""",
            [P("city", Text, "City: Yangon, Mandalay or Naypyitaw"), P("gender", Text, "Male or Female"), From("purchase-date"), To("purchase-date")],
            ["market_line"], "tune-033", """{"city": "Yangon", "gender": "Male", "from": "2019-02-01", "to": "2019-02-28"}"""),
        Aggregate("suppliers_by_product_count", "Suppliers ranked by how many products of one category they launched in a date range, most first.",
            """{"table": "@products", "aggregates": [{"aggregate": "count"}], "group_by": ["supplier"], "where": {"and": [{"column": "category", "op": "=", "value": {"param": "category"}}, {"column": "launched", "op": "between", "values": [{"param": "from"}, {"param": "to"}]}]}, "order_by": [{"aggregate": "count", "direction": "desc"}, {"column": "supplier", "direction": "asc"}], "limit": 10}""",
            [P("category", Text, "Product category, such as Training"), From("launch-date"), To("launch-date")],
            ["supplier_products"], "tune-038", """{"category": "Training", "from": "2020-01-01", "to": "2020-12-31"}"""),
        Aggregate("bank_utilization_by_education", "Education levels ranked by the average utilization ratio of bank customers of one gender in one income category, highest first.",
            """{"table": "@bank", "aggregates": [{"aggregate": "avg", "column": "avg_utilization_ratio"}], "group_by": ["education_level"], "where": {"and": [{"column": "income_category", "op": "=", "value": {"param": "income_category"}}, {"column": "gender", "op": "=", "value": {"param": "gender"}}]}, "order_by": [{"aggregate": "avg", "column": "avg_utilization_ratio", "direction": "desc"}, {"column": "education_level", "direction": "asc"}], "limit": 10}""",
            [P("income_category", Text, "Income category exactly as stored, such as Less than $40K"), P("gender", Text, "M or F")],
            ["bank_education"], "tune-040", """{"income_category": "Less than $40K", "gender": "M"}"""),

        // Joins.
        Aggregate("order_value_by_customer_segment", "Total value of orders of one status, in a date range, from customers of one segment in one country.",
            """{"table": "@orders", "join": {"table": "@customers", "as": "c", "column": "customer_id", "join_column": "customer_id", "kind": "inner"}, "aggregates": [{"aggregate": "sum", "column": "total"}], "where": {"and": [{"column": "status", "op": "=", "value": {"param": "status"}}, {"column": "c.segment", "op": "=", "value": {"param": "segment"}}, {"column": "c.country", "op": "=", "value": {"param": "country"}}, {"column": "order_date", "op": "between", "values": [{"param": "from"}, {"param": "to"}]}]}}""",
            [P("segment", Text, "Customer segment, such as Mid-Market"), P("country", Text, "Customer country, such as Australia"), P("status", Text, "Order status", "\"completed\""), From("order-date"), To("order-date")],
            ["segment_revenue"], "tune-041", """{"segment": "Mid-Market", "country": "Australia", "from": "2025-01-01", "to": "2025-12-31"}"""),
        Aggregate("order_value_by_supplier", "Total value of orders of one status, in a date range, for products from one supplier.",
            """{"table": "@orders", "join": {"table": "@products", "as": "p", "column": "product_id", "join_column": "product_id", "kind": "inner"}, "aggregates": [{"aggregate": "sum", "column": "total"}], "where": {"and": [{"column": "status", "op": "=", "value": {"param": "status"}}, {"column": "p.supplier", "op": "=", "value": {"param": "supplier"}}, {"column": "order_date", "op": "between", "values": [{"param": "from"}, {"param": "to"}]}]}}""",
            [P("supplier", Text, "Supplier, such as Northwind Components"), P("status", Text, "Order status", "\"completed\""), From("order-date"), To("order-date")],
            ["supplier_revenue"], "tune-042", """{"supplier": "Northwind Components", "from": "2024-07-01", "to": "2024-07-31"}"""),
        Aggregate("order_count_by_customer_name", "How many orders one customer, named as in the customers table, placed in a date range.",
            """{"table": "@orders", "join": {"table": "@customers", "as": "c", "column": "customer_id", "join_column": "customer_id", "kind": "inner"}, "aggregates": [{"aggregate": "count"}], "where": {"and": [{"column": "c.name", "op": "=", "value": {"param": "customer"}}, {"column": "order_date", "op": "between", "values": [{"param": "from"}, {"param": "to"}]}]}}""",
            [P("customer", Text, "Customer name, such as Vertex Insurance Inc"), From("order-date"), To("order-date")],
            ["customer_orders"], "tune-043", """{"customer": "Vertex Insurance Inc", "from": "2023-01-01", "to": "2023-12-31"}"""),
        Aggregate("units_ordered_by_category_channel", "Total units ordered of one product category through one sales channel in a date range.",
            """{"table": "@orders", "join": {"table": "@products", "as": "p", "column": "product_id", "join_column": "product_id", "kind": "inner"}, "aggregates": [{"aggregate": "sum", "column": "quantity"}], "where": {"and": [{"column": "p.category", "op": "=", "value": {"param": "category"}}, {"column": "channel", "op": "=", "value": {"param": "channel"}}, {"column": "order_date", "op": "between", "values": [{"param": "from"}, {"param": "to"}]}]}}""",
            [P("category", Text, "Product category, such as Hardware"), P("channel", Text, "Sales channel, such as direct, partner or online"), From("order-date"), To("order-date")],
            ["category_units"], "tune-044", """{"category": "Hardware", "channel": "direct", "from": "2025-04-01", "to": "2025-04-30"}"""),
        Aggregate("invoice_amount_by_customer_segment_region", "Total amount of invoices of one status, issued in a date range, to customers of one segment in one region.",
            """{"table": "@invoices", "join": {"table": "@customers", "as": "c", "column": "customer_id", "join_column": "customer_id", "kind": "inner"}, "aggregates": [{"aggregate": "sum", "column": "amount"}], "where": {"and": [{"column": "status", "op": "=", "value": {"param": "status"}}, {"column": "c.segment", "op": "=", "value": {"param": "segment"}}, {"column": "c.region", "op": "=", "value": {"param": "region"}}, {"column": "issued_on", "op": "between", "values": [{"param": "from"}, {"param": "to"}]}]}}""",
            [P("segment", Text, "Customer segment, such as Enterprise"), P("region", Text, "Customer region, such as APAC"), P("status", Text, "Invoice status, such as overdue", "\"overdue\""), From("issue-date"), To("issue-date")],
            ["overdue_region"], "tune-045", """{"segment": "Enterprise", "region": "APAC", "from": "2025-01-01", "to": "2025-12-31"}"""),
        Aggregate("customers_by_account_manager", "How many customers of one segment an employee manages as account manager, by the employee's full name.",
            """{"table": "@customers", "join": {"table": "@employees", "as": "e", "column": "account_manager_id", "join_column": "employee_id", "kind": "inner"}, "aggregates": [{"aggregate": "count"}], "where": {"and": [{"column": "e.full_name", "op": "=", "value": {"param": "manager"}}, {"column": "segment", "op": "=", "value": {"param": "segment"}}]}}""",
            [P("manager", Text, "The account manager's full name, such as Caleb Nakamura F."), P("segment", Text, "Customer segment, such as SMB")],
            ["manager_customers"], "tune-048", """{"manager": "Caleb Nakamura F.", "segment": "SMB"}"""),
    ];

    /// <summary>The templates the library answers.</summary>
    public static IReadOnlySet<string> Templates { get; } = Queries.SelectMany(q => q.Templates).ToHashSet(StringComparer.Ordinal);

    /// <summary>The tables the library reads, by name.</summary>
    public static IReadOnlySet<string> Tables { get; } = Queries
        .SelectMany(q => TableRef().Matches(q.Definition.Request).Select(m => m.Groups[1].Value))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary><paramref name="definition"/> with each <c>@&lt;table&gt;</c> replaced by that table's id.</summary>
    public static SavedQueryDefinition Resolve(SavedQueryDefinition definition, IReadOnlyDictionary<string, Guid> tableIds) =>
        definition with
        {
            Request = TableRef().Replace(definition.Request, m => tableIds.TryGetValue(m.Groups[1].Value, out Guid id)
                ? $"\"{id}\""
                : throw new InvalidOperationException($"The library names table '{m.Groups[1].Value}', which the container doesn't have.")),
        };

    [GeneratedRegex("\"@([a-z0-9-]+)\"")]
    private static partial Regex TableRef();
}
