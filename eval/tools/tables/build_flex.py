"""Builds the tables flexibility set (CON-336): questions that need more than lookups and plain aggregates, so the
three ways an agent can query tables (structured tools, SQL, GraphQL) are compared on what each can express.

Six seeded files go in eval/datasets/tables/flex/docs, all for one fictional retailer:
  stores.csv    12 stores with a city and region;
  staff.csv     60 staff, each at one store, with a role;
  sales.csv     2,400 sales: staff, product, date, units, unit and cost price, and a discount that is often blank;
  returns.csv   returned sales with a date and reason;
  meters.csv    weekly electricity readings per store whose kwh column mixes numbers with "n/a", "error" and
                "estimated", so it is a text column that mostly holds numbers;
  catalog.json  a JSON array of 80 products with nested specs (weight, dimensions), a list of variants (colour,
                stock, price) and tags.

Each file's records are also loaded into SQLite as plain relational tables, written here independently of Connapse's
loader, and every gold answer comes from a SQL query over them (saved with the question as gold_sql). meters gets an
extra kwh_num column, the kwh value when it is a number, so the gold SQL can say "numeric readings" plainly.

Questions: seven families (nested_json, mixed_kind, column_math, median, rank_in_group, monthly_total,
three_table_join), 12 per family. The first 2 of each family are the tune split and the other 10 the test split.
A ranked answer is rejected if the value at its rank ties a neighbour's.

Usage:
    python eval/tools/tables/build_flex.py
"""
import json
import os
import random
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(__file__))
from build_tables import FIRST, LAST  # noqa: E402
from common import REPO_DIR, write_csv  # noqa: E402

FLEX_DIR = os.path.join(REPO_DIR, "flex")
DOCS_DIR = os.path.join(FLEX_DIR, "docs")
SEED = 20261113
PER_FAMILY = 12
TUNE_PER_FAMILY = 2

CITIES = {"Leeds": "north", "York": "north", "Durham": "north", "Bristol": "west", "Exeter": "west", "Bath": "west",
          "Norwich": "east", "Ipswich": "east", "Cambridge": "east", "Brighton": "south", "Dover": "south",
          "Portsmouth": "south"}
ROLES = ["associate", "associate", "associate", "lead", "manager"]
CATEGORIES = ["audio", "kitchen", "garden", "office", "toys"]
COLORS = ["black", "white", "red", "blue", "green", "grey"]
TAGS = ["new", "eco", "bestseller", "clearance", "bundle"]
REASONS = ["damaged", "wrong size", "changed mind", "late delivery", "faulty"]
NOT_NUMBERS = ["n/a", "error", "estimated"]
MONTHS = [f"2026-{m:02d}" for m in range(1, 10)]


def day(rng, month=None):
    month = month or rng.randint(1, 9)
    return f"2026-{month:02d}-{rng.randint(1, 28):02d}"


# ---------------------------------------------------------------- data


def build(rng):
    stores = [{"store_id": f"S{n + 1:02d}", "city": city, "region": region, "opened": f"{rng.randint(2005, 2022)}-{rng.randint(1, 12):02d}-01"}
              for n, (city, region) in enumerate(CITIES.items())]

    names = set()
    staff = []
    for n in range(60):
        while True:
            name = f"{rng.choice(FIRST)} {rng.choice(LAST)}"
            if name not in names:
                names.add(name)
                break
        staff.append({"staff_id": f"E-{101 + n}", "name": name, "store_id": stores[n % len(stores)]["store_id"],
                      "role": "manager" if n < len(stores) else rng.choice(ROLES[:4]),
                      "hired": f"{rng.randint(2010, 2025)}-{rng.randint(1, 12):02d}-{rng.randint(1, 28):02d}"})

    catalog = []
    for n in range(80):
        variants = [{"color": c, "stock": rng.randint(0, 140), "price": round(rng.uniform(4, 400), 2)}
                    for c in rng.sample(COLORS, rng.randint(1, 4))]
        product = {
            "sku": f"P-{1001 + n}",
            "name": f"{rng.choice(['Compact', 'Pro', 'Classic', 'Mini', 'Max', 'Eco'])} {rng.choice(['Speaker', 'Kettle', 'Planter', 'Lamp', 'Desk Tidy', 'Blender', 'Hose', 'Robot', 'Puzzle', 'Chair'])}",
            "category": CATEGORIES[n % len(CATEGORIES)],
            "specs": {"weight_kg": round(rng.uniform(0.2, 25), 2),
                      "dims": {"w_cm": rng.randint(5, 120), "h_cm": rng.randint(5, 120), "d_cm": rng.randint(5, 80)}},
            "variants": variants,
        }
        tags = rng.sample(TAGS, rng.choice([0, 1, 1, 2]))
        if tags:
            product["tags"] = tags
        catalog.append(product)

    sales = []
    for n in range(2400):
        product = rng.choice(catalog)
        base = min(v["price"] for v in product["variants"])
        price = round(base * rng.uniform(0.9, 1.1), 2)
        sales.append({"sale_id": 500001 + n, "staff_id": rng.choice(staff)["staff_id"], "sku": product["sku"],
                      "sold_on": day(rng), "units": rng.randint(1, 8), "unit_price": price,
                      "cost_price": round(price * rng.uniform(0.45, 0.8), 2),
                      "discount_pct": rng.choice([None, None, None, 5, 10, 15, 20])})
    sales.sort(key=lambda s: s["sale_id"])

    returns = []
    for s in rng.sample(sales, 180):
        sold_month = int(s["sold_on"][5:7])
        returns.append({"sale_id": s["sale_id"], "returned_on": day(rng, min(9, sold_month + rng.choice([0, 0, 1]))),
                        "reason": rng.choice(REASONS)})
    returns.sort(key=lambda r: r["sale_id"])

    meters = []
    for st in stores:
        for week in range(36):
            month = 1 + week * 9 // 36
            date = f"2026-{month:02d}-{1 + (week % 4) * 7:02d}"
            kwh = rng.choice(NOT_NUMBERS) if rng.random() < 0.12 else str(round(rng.uniform(180, 900), 1))
            meters.append({"store_id": st["store_id"], "read_on": date, "kwh": kwh})
    return stores, staff, catalog, sales, returns, meters


def number(text):
    try:
        return float(text)
    except ValueError:
        return None


# ---------------------------------------------------------------- reference tables


def reference(stores, staff, catalog, sales, returns, meters):
    con = sqlite3.connect(":memory:")
    c = con.cursor()
    c.executescript("""
        CREATE TABLE stores(store_id TEXT, city TEXT, region TEXT, opened TEXT);
        CREATE TABLE staff(staff_id TEXT, name TEXT, store_id TEXT, role TEXT, hired TEXT);
        CREATE TABLE products(sku TEXT, name TEXT, category TEXT, weight_kg REAL, w_cm INTEGER, h_cm INTEGER, d_cm INTEGER);
        CREATE TABLE variants(sku TEXT, color TEXT, stock INTEGER, price REAL);
        CREATE TABLE tags(sku TEXT, tag TEXT);
        CREATE TABLE sales(sale_id INTEGER, staff_id TEXT, sku TEXT, sold_on TEXT, units INTEGER, unit_price REAL, cost_price REAL, discount_pct INTEGER);
        CREATE TABLE returns(sale_id INTEGER, returned_on TEXT, reason TEXT);
        CREATE TABLE meters(store_id TEXT, read_on TEXT, kwh TEXT, kwh_num REAL);
    """)
    c.executemany("INSERT INTO stores VALUES (?,?,?,?)", [(s["store_id"], s["city"], s["region"], s["opened"]) for s in stores])
    c.executemany("INSERT INTO staff VALUES (?,?,?,?,?)", [(s["staff_id"], s["name"], s["store_id"], s["role"], s["hired"]) for s in staff])
    for p in catalog:
        d = p["specs"]["dims"]
        c.execute("INSERT INTO products VALUES (?,?,?,?,?,?,?)", (p["sku"], p["name"], p["category"], p["specs"]["weight_kg"], d["w_cm"], d["h_cm"], d["d_cm"]))
        for v in p["variants"]:
            c.execute("INSERT INTO variants VALUES (?,?,?,?)", (p["sku"], v["color"], v["stock"], v["price"]))
        for t in p.get("tags", []):
            c.execute("INSERT INTO tags VALUES (?,?)", (p["sku"], t))
    c.executemany("INSERT INTO sales VALUES (?,?,?,?,?,?,?,?)", [(s["sale_id"], s["staff_id"], s["sku"], s["sold_on"], s["units"], s["unit_price"],
                                                                   s["cost_price"], s["discount_pct"]) for s in sales])
    c.executemany("INSERT INTO returns VALUES (?,?,?)", [(r["sale_id"], r["returned_on"], r["reason"]) for r in returns])
    c.executemany("INSERT INTO meters VALUES (?,?,?,?)", [(m["store_id"], m["read_on"], m["kwh"], number(m["kwh"])) for m in meters])
    con.commit()
    return con


# ---------------------------------------------------------------- questions

REVENUE = "s.units * s.unit_price * (1 - COALESCE(s.discount_pct, 0) / 100.0)"
REVENUE_NOTE = "revenue is units times unit price, less the discount percentage; a blank discount means none"


class Family:
    """Draws questions for one family from its templates in turn, keeping only those with a usable answer."""

    def __init__(self, con, name):
        self.con = con
        self.name = name
        self.items = []
        self.seen = set()

    def add(self, template, question, answer_type, sql, params, files):
        if (template, question) in self.seen:
            return False
        rows = self.con.execute(sql, params).fetchall()
        if len(rows) != 1 or rows[0][0] is None:
            return False
        answer = rows[0][0]
        if answer_type == "number":
            if answer == 0:
                return False
            answer = round(answer, 4) if isinstance(answer, float) else answer
        return self.keep(template, question, answer_type, answer, inline(sql, params), files)

    def add_ranked(self, template, question, sql, params, k, files):
        """The key at rank k (1 = first) of a query returning (key, value) best first; refused if the value ties."""
        if (template, question) in self.seen:
            return False
        rows = self.con.execute(sql, params).fetchall()
        if len(rows) < k + 1 or any(r[1] is None for r in rows):
            return False
        values = [r[1] for r in rows]
        if values[k - 1] == values[k] or (k >= 2 and values[k - 2] == values[k - 1]):
            return False
        return self.keep(template, question, "category", rows[k - 1][0], inline(sql, params) + f" LIMIT 1 OFFSET {k - 1}", files)

    def add_rank_of(self, template, question, sql, params, key, files):
        """The rank (1 = first) of key in a query returning (key, value) best first; refused if its value ties."""
        if (template, question) in self.seen:
            return False
        rows = self.con.execute(sql, params).fetchall()
        keys = [r[0] for r in rows]
        if key not in keys:
            return False
        i = keys.index(key)
        value = rows[i][1]
        if sum(1 for r in rows if r[1] == value) > 1 or i == 0:
            return False
        gold = f"WITH ranked AS ({inline(sql, params)}) SELECT COUNT(*) + 1 FROM ranked WHERE total > (SELECT total FROM ranked WHERE k = '{key}')"
        return self.keep(template, question, "number", i + 1, gold, files)

    def keep(self, template, question, answer_type, answer, gold_sql, files):
        self.seen.add((template, question))
        self.items.append({"family": self.name, "template": template, "question": question, "answer_type": answer_type,
                           "answer": answer, "gold_sql": gold_sql, "files": files})
        return True


def inline(sql, params):
    """The gold SQL with its parameters written in, so a reader can run it as is."""
    for p in params:
        sql = sql.replace("?", f"'{p}'" if isinstance(p, str) else str(p), 1)
    return " ".join(sql.split())


def fill(family, rng, makers):
    """Cycles through the family's templates, one question each turn, until the family has PER_FAMILY questions."""
    attempts = 0
    while len(family.items) < PER_FAMILY:
        make = makers[len(family.items) % len(makers)]
        attempts += 1
        if attempts > 5000:
            raise RuntimeError(f"{family.name}: could not draw {PER_FAMILY} questions")
        make(rng)
    return family.items


def month_name(month):
    return ["January", "February", "March", "April", "May", "June", "July", "August", "September"][int(month[5:7]) - 1] + " 2026"


def questions(con, rng, stores, staff, catalog, sales):
    store_ids = [s["store_id"] for s in stores]
    cities = list(CITIES)
    regions = sorted(set(CITIES.values()))
    skus = [p["sku"] for p in catalog]
    sold_skus = sorted({s["sku"] for s in sales})
    out = []

    f = Family(con, "nested_json")
    fill(f, rng, [
        lambda r: (lambda c: f.add("category_stock", f"What is the total stock across every variant of every {c} product in the catalog?", "number",
                                   "SELECT SUM(v.stock) FROM variants v JOIN products p ON p.sku = v.sku WHERE p.category = ?", [c], ["catalog.json"]))(r.choice(CATEGORIES)),
        lambda r: (lambda p: (lambda v: f.add("variant_price", f"What is the price of the {v['color']} variant of product {p['sku']}?", "number",
                                              "SELECT price FROM variants WHERE sku = ? AND color = ?", [p["sku"], v["color"]], ["catalog.json"]))(r.choice(p["variants"])))(r.choice(catalog)),
        lambda r: (lambda c, w: f.add("heavy_count", f"How many {c} products in the catalog weigh more than {w} kg?", "number",
                                      "SELECT COUNT(*) FROM products WHERE category = ? AND weight_kg > ?", [c, w], ["catalog.json"]))(r.choice(CATEGORIES), r.choice([5, 10, 15])),
        lambda r: (lambda s: f.add("volume", f"What is the volume in cubic centimetres (width times height times depth) of product {s}?", "number",
                                   "SELECT w_cm * h_cm * d_cm FROM products WHERE sku = ?", [s], ["catalog.json"]))(r.choice(skus)),
    ])
    out += f.items

    f = Family(con, "mixed_kind")
    fill(f, rng, [
        lambda r: (lambda s: f.add("meter_avg", f"What is the average of the numeric kWh readings for store {s}? Ignore readings that aren't numbers.", "number",
                                   "SELECT AVG(kwh_num) FROM meters WHERE store_id = ?", [s], ["meters.csv"]))(r.choice(store_ids)),
        lambda r: (lambda s: f.add("meter_bad", f"How many of store {s}'s kWh readings are not numbers?", "number",
                                   "SELECT COUNT(*) FROM meters WHERE store_id = ? AND kwh_num IS NULL", [s], ["meters.csv"]))(r.choice(store_ids)),
        lambda r: (lambda s: f.add("meter_max", f"What is the highest numeric kWh reading for store {s}?", "number",
                                   "SELECT MAX(kwh_num) FROM meters WHERE store_id = ?", [s], ["meters.csv"]))(r.choice(store_ids)),
        lambda r: (lambda s, m: f.add("meter_month", f"What is the total of store {s}'s numeric kWh readings taken in {month_name(m)}? Ignore readings that aren't numbers.", "number",
                                      "SELECT SUM(kwh_num) FROM meters WHERE store_id = ? AND substr(read_on, 1, 7) = ?", [s, m], ["meters.csv"]))(r.choice(store_ids), r.choice(MONTHS)),
    ])
    out += f.items

    f = Family(con, "column_math")
    fill(f, rng, [
        lambda r: (lambda e: f.add("staff_revenue", f"What is the total revenue of the sales made by staff member {e['staff_id']}, where {REVENUE_NOTE}?", "number",
                                   f"SELECT SUM({REVENUE}) FROM sales s WHERE s.staff_id = ?", [e["staff_id"]], ["sales.csv"]))(r.choice(staff)),
        lambda r: (lambda p: f.add("sku_margin", f"What is the total margin on sales of product {p}, where margin is units times (unit price minus cost price)?", "number",
                                   "SELECT SUM(s.units * (s.unit_price - s.cost_price)) FROM sales s WHERE s.sku = ?", [p], ["sales.csv"]))(r.choice(sold_skus)),
        lambda r: (lambda p, v: f.add("big_sales", f"How many sales of product {p} had a revenue above {v}, where {REVENUE_NOTE}?", "number",
                                      f"SELECT COUNT(*) FROM sales s WHERE s.sku = ? AND {REVENUE} > ?", [p, v], ["sales.csv"]))(r.choice(sold_skus), r.choice([100, 250, 500])),
        lambda r: (lambda m: f.add("discount_given", f"In {month_name(m)}, how much money was given away in discounts in total (units times unit price times the discount percentage)?", "number",
                                   "SELECT SUM(s.units * s.unit_price * s.discount_pct / 100.0) FROM sales s WHERE substr(s.sold_on, 1, 7) = ?", [m], ["sales.csv"]))(r.choice(MONTHS)),
    ])
    out += f.items

    median = "SELECT AVG(x) FROM (SELECT x, ROW_NUMBER() OVER (ORDER BY x) AS rn, COUNT(*) OVER () AS c FROM ({inner})) WHERE rn IN ((c + 1) / 2, (c + 2) / 2)"
    f = Family(con, "median")
    fill(f, rng, [
        lambda r: (lambda e: f.add("staff_median_price", f"What is the median unit price of the sales made by staff member {e['staff_id']}?", "number",
                                   median.format(inner="SELECT unit_price AS x FROM sales WHERE staff_id = ?"), [e["staff_id"]], ["sales.csv"]))(r.choice(staff)),
        lambda r: (lambda c: f.add("category_median_weight", f"What is the median weight in kg of the {c} products in the catalog?", "number",
                                   median.format(inner="SELECT weight_kg AS x FROM products WHERE category = ?"), [c], ["catalog.json"]))(r.choice(CATEGORIES)),
        lambda r: (lambda s: f.add("store_median_kwh", f"What is the median of store {s}'s numeric kWh readings? Ignore readings that aren't numbers.", "number",
                                   median.format(inner="SELECT kwh_num AS x FROM meters WHERE store_id = ? AND kwh_num IS NOT NULL"), [s], ["meters.csv"]))(r.choice(store_ids)),
        lambda r: (lambda p: f.add("sku_median_units", f"What is the median number of units per sale for product {p}?", "number",
                                   median.format(inner="SELECT units AS x FROM sales WHERE sku = ?"), [p], ["sales.csv"]))(r.choice(sold_skus)),
    ])
    out += f.items

    units_by_staff = ("SELECT e.name AS k, SUM(s.units) AS total FROM sales s JOIN staff e ON e.staff_id = s.staff_id "
                      "WHERE e.store_id = ? GROUP BY e.name ORDER BY total DESC")
    f = Family(con, "rank_in_group")
    fill(f, rng, [
        lambda r: (lambda s, k: f.add_ranked("store_kth_seller", f"At store {s}, which staff member sold the {['', 'most', 'second-most', 'third-most'][k]} units in total? Give their name.",
                                             units_by_staff, [s], k, ["sales.csv", "staff.csv"]))(r.choice(store_ids), r.choice([1, 2, 3])),
        lambda r: (lambda e: f.add_rank_of("staff_rank", f"Ranked by total units sold among the staff of their own store (1 = most), what rank is {e['name']}?",
                                           units_by_staff, [e["store_id"]], e["name"], ["sales.csv", "staff.csv"]))(r.choice(staff)),
        lambda r: (lambda c: f.add_ranked("category_top_sku", f"Which {c} product sold the most units in total? Give its SKU.",
                                          "SELECT s.sku AS k, SUM(s.units) AS total FROM sales s JOIN products p ON p.sku = s.sku WHERE p.category = ? GROUP BY s.sku ORDER BY total DESC",
                                          [c], 1, ["sales.csv", "catalog.json"]))(r.choice(CATEGORIES)),
        lambda r: (lambda g, k: f.add_ranked("region_kth_store", f"Among stores in the {g} region, which store had the {['', 'highest', 'second-highest'][k]} revenue, where {REVENUE_NOTE}? Give its store id.",
                                             f"SELECT e.store_id AS k, SUM({REVENUE}) AS total FROM sales s JOIN staff e ON e.staff_id = s.staff_id JOIN stores t ON t.store_id = e.store_id WHERE t.region = ? GROUP BY e.store_id ORDER BY total DESC",
                                             [g], k, ["sales.csv", "staff.csv", "stores.csv"]))(r.choice(regions), r.choice([1, 2])),
    ])
    out += f.items

    f = Family(con, "monthly_total")
    fill(f, rng, [
        lambda r: (lambda s, m: f.add("store_month_units", f"How many units did the staff of store {s} sell in {month_name(m)}?", "number",
                                      "SELECT SUM(s.units) FROM sales s JOIN staff e ON e.staff_id = s.staff_id WHERE e.store_id = ? AND substr(s.sold_on, 1, 7) = ?", [s, m], ["sales.csv", "staff.csv"]))(r.choice(store_ids), r.choice(MONTHS)),
        lambda r: (lambda e: f.add_ranked("staff_best_month", f"In which month did staff member {e['staff_id']} have the highest revenue, where {REVENUE_NOTE}? Answer as YYYY-MM.",
                                          f"SELECT substr(s.sold_on, 1, 7) AS k, SUM({REVENUE}) AS total FROM sales s WHERE s.staff_id = ? GROUP BY k ORDER BY total DESC",
                                          [e["staff_id"]], 1, ["sales.csv"]))(r.choice(staff)),
        lambda r: (lambda m: f.add("month_returns", f"How many sales were returned in {month_name(m)}?", "number",
                                   "SELECT COUNT(*) FROM returns WHERE substr(returned_on, 1, 7) = ?", [m], ["returns.csv"]))(r.choice(MONTHS)),
        lambda r: (lambda p, m: f.add("sku_month_revenue", f"What was the revenue of product {p} in {month_name(m)}, where {REVENUE_NOTE}?", "number",
                                      f"SELECT SUM({REVENUE}) FROM sales s WHERE s.sku = ? AND substr(s.sold_on, 1, 7) = ?", [p, m], ["sales.csv"]))(r.choice(sold_skus), r.choice(MONTHS)),
    ])
    out += f.items

    f = Family(con, "three_table_join")
    fill(f, rng, [
        lambda r: (lambda g, c: f.add("region_category_units", f"How many units of {c} products did staff at stores in the {g} region sell?", "number",
                                      "SELECT SUM(s.units) FROM sales s JOIN staff e ON e.staff_id = s.staff_id JOIN stores t ON t.store_id = e.store_id "
                                      "JOIN products p ON p.sku = s.sku WHERE t.region = ? AND p.category = ?", [g, c], ["sales.csv", "staff.csv", "stores.csv", "catalog.json"]))(r.choice(regions), r.choice(CATEGORIES)),
        lambda r: (lambda c: f.add("city_returns", f"How many returned sales were made by staff of the store in {c}?", "number",
                                   "SELECT COUNT(*) FROM returns x JOIN sales s ON s.sale_id = x.sale_id JOIN staff e ON e.staff_id = s.staff_id "
                                   "JOIN stores t ON t.store_id = e.store_id WHERE t.city = ?", [c], ["returns.csv", "sales.csv", "staff.csv", "stores.csv"]))(r.choice(cities)),
        lambda r: (lambda reason, g: f.add("reason_region", f"How many sales made at stores in the {g} region were returned with the reason \"{reason}\"?", "number",
                                           "SELECT COUNT(*) FROM returns x JOIN sales s ON s.sale_id = x.sale_id JOIN staff e ON e.staff_id = s.staff_id "
                                           "JOIN stores t ON t.store_id = e.store_id WHERE x.reason = ? AND t.region = ?", [reason, g], ["returns.csv", "sales.csv", "staff.csv", "stores.csv"]))(r.choice(REASONS), r.choice(regions)),
        lambda r: (lambda c, role: f.add("role_category_products", f"How many different {c} products were sold by staff with the role {role}?", "number",
                                         "SELECT COUNT(DISTINCT s.sku) FROM sales s JOIN staff e ON e.staff_id = s.staff_id JOIN products p ON p.sku = s.sku "
                                         "WHERE p.category = ? AND e.role = ?", [c, role], ["sales.csv", "staff.csv", "catalog.json"]))(r.choice(CATEGORIES), r.choice(["lead", "manager"])),
    ])
    out += f.items
    return out


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def main():
    rng = random.Random(SEED)
    stores, staff, catalog, sales, returns, meters = build(rng)

    def rows(records, header):
        return header, [[r[h] for h in header] for r in records]

    for name, records, header in [
        ("stores.csv", stores, ["store_id", "city", "region", "opened"]),
        ("staff.csv", staff, ["staff_id", "name", "store_id", "role", "hired"]),
        ("sales.csv", sales, ["sale_id", "staff_id", "sku", "sold_on", "units", "unit_price", "cost_price", "discount_pct"]),
        ("returns.csv", returns, ["sale_id", "returned_on", "reason"]),
        ("meters.csv", meters, ["store_id", "read_on", "kwh"]),
    ]:
        write_csv(os.path.join(DOCS_DIR, name), *rows(records, header))
    write(os.path.join(DOCS_DIR, "catalog.json"), json.dumps(catalog, indent=2) + "\n")

    con = reference(stores, staff, catalog, sales, returns, meters)
    items = questions(con, random.Random(SEED + 1), stores, staff, catalog, sales)
    lines = []
    counters = {"tune": 0, "test": 0}
    by_family = {}
    for q in items:
        by_family.setdefault(q["family"], []).append(q)
    for family, qs in by_family.items():
        for n, q in enumerate(qs):
            split = "tune" if n < TUNE_PER_FAMILY else "test"
            counters[split] += 1
            lines.append({"id": f"flex-{split}-{counters[split]:03d}", "split": split, **q})
    lines.sort(key=lambda q: (q["split"] != "tune", q["id"]))
    write(os.path.join(FLEX_DIR, "questions.jsonl"), "".join(json.dumps(q, ensure_ascii=False) + "\n" for q in lines))
    print(f"{len(lines)} questions: {counters['tune']} tune, {counters['test']} test")


if __name__ == "__main__":
    main()
