# tables-v1 nested set (CON-335)

Four synthetic files whose answers sit in the parent and child tables Connapse loads from nested data. Every name,
order, reading, book and van is invented by `eval/tools/tables/build_nested.py` (seed 20261112); rerunning it rewrites
these files byte for byte.

| File | Shape |
|------|-------|
| `docs/orders.json` | JSON array of 150 orders: nested customer and address, order lines with a discounts list, optional tags |
| `docs/readings.jsonl` | JSON Lines, 220 sensor readings: nested site and metrics, alerts, labels, and a calibration matrix (an array of arrays, stored as jsonb) |
| `docs/library.xml` | 4 branches of 30 books, with attributes, repeated keyword elements and loan elements |
| `docs/fleet.yaml` | top-level YAML list of 60 vans: nested depot, services with a parts list, drivers |

`questions.jsonl` holds 72 questions in six families (lookup, filter_count, child_aggregate, grandchild, scalar_list,
json): 2 per family in `tune`, 10 per family in `test`. Each gold answer comes from the SQL in its `gold_sql`, run
against SQLite tables the builder fills from the same records, independently of Connapse's loader.

The pass bar is "Bar for nested files" in `eval/pass-bars.md`, written before these questions existed. Run it with
`dotnet run --project tests/Connapse.Eval -- tables-nested --split test`.
