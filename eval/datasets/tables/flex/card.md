# tables flexibility set (CON-336)

Six synthetic files for one invented retailer, built to test what each way of querying tables can express: the
structured table tools, agent-written SQL (`table_sql`) and GraphQL (`table_graphql`). Every name, store, sale and
product is invented by `eval/tools/tables/build_flex.py` (seed 20261113); rerunning it rewrites these files byte for
byte.

| File | Shape |
|------|-------|
| `docs/stores.csv` | 12 stores: id, city, region, opening date |
| `docs/staff.csv` | 60 staff, each at one store, with a role and hire date |
| `docs/sales.csv` | 2,400 sales: staff, product, date, units, unit price, cost price, and a discount percentage that is blank on about half the rows |
| `docs/returns.csv` | 180 returned sales with a date and reason |
| `docs/meters.csv` | 432 weekly electricity readings; the kwh column mixes numbers with "n/a", "error" and "estimated", so it loads as text |
| `docs/catalog.json` | JSON array of 80 products: nested specs (weight, dimensions), a variants list (colour, stock, price), optional tags |

`questions.jsonl` holds 84 questions in seven families, four templates each: nested_json, mixed_kind, column_math
(arithmetic across columns), median, rank_in_group, monthly_total and three_table_join. 2 per family are `tune` and
10 per family are `test`. Each gold answer comes from the SQL in its `gold_sql`, run against SQLite tables the builder
fills from the same records, independently of Connapse's loader. A ranked answer is dropped when its value ties a
neighbour's, and questions say how to treat blank discounts and readings that aren't numbers.

The pass bar is "Bar for the flexibility set" in `eval/pass-bars.md`, written before any run.
