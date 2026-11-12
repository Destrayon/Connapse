"""Builds the tables-v1 nested set (CON-335): nested JSON, JSON Lines, XML and YAML files whose answers need the
parent and child tables Connapse loads from them.

Four seeded files go in eval/datasets/tables/nested/docs:
  orders.json     a JSON array of orders, each with a nested customer, order lines with discounts, and tags;
  readings.jsonl  JSON Lines sensor readings with a nested site and metrics, alerts, labels, and a calibration matrix
                  (an array of arrays, which can't be flattened);
  library.xml     branches holding books, each with attributes, loans and keywords;
  fleet.yaml      a top-level list of vehicles with a nested depot, services with parts, and drivers.

Each file's records are also loaded into SQLite as plain relational tables, written here independently of Connapse's
loader, and every gold answer comes from a SQL query over them (saved with the question as gold_sql).

Questions: six families (lookup, filter_count, child_aggregate, grandchild, scalar_list, json), 12 per family. The
first 2 of each family are the tune split and the other 10 the test split, as eval/pass-bars.md ("Bar for nested
files") fixes.

Usage:
    python eval/tools/tables/build_nested.py
"""
import json
import os
import random
import sqlite3
import sys
from xml.sax.saxutils import escape, quoteattr

sys.path.insert(0, os.path.dirname(__file__))
from build_tables import FIRST, LAST  # noqa: E402
from common import REPO_DIR  # noqa: E402

NESTED_DIR = os.path.join(REPO_DIR, "nested")
DOCS_DIR = os.path.join(NESTED_DIR, "docs")
SEED = 20261112
PER_FAMILY = 12
TUNE_PER_FAMILY = 2

COUNTRIES = {"Germany": ["Berlin", "Hamburg", "Munich"], "France": ["Paris", "Lyon", "Lille"],
             "Spain": ["Madrid", "Seville", "Valencia"], "Italy": ["Rome", "Milan", "Turin"],
             "Poland": ["Warsaw", "Krakow", "Gdansk"]}
STATUSES = ["placed", "shipped", "delivered", "returned"]
TIERS = ["gold", "silver", "bronze"]
SKUS = [f"SKU-{n}" for n in range(101, 131)]
DISCOUNTS = ["SPRING10", "LOYAL5", "BULK15", "WELCOME"]
ORDER_TAGS = ["gift", "express", "fragile", "bulk", "b2b"]

SITES = {"north-yard": "north", "harbor": "north", "east-mill": "east", "river-plant": "east", "south-depot": "south",
         "dune-field": "south", "west-lab": "west", "canyon": "west"}
ALERT_CODES = ["A10", "A12", "A17", "A21", "A30"]
LABELS = ["outdoor", "indoor", "backup", "reviewed", "noisy"]

BRANCHES = [("Northside", "Leeds"), ("Riverside", "York"), ("Old Town", "Bath"), ("Harbour", "Hull")]
GENRES = ["mystery", "history", "science", "poetry", "travel", "fantasy"]
KEYWORDS = ["award", "classic", "local", "illustrated", "debut", "translated"]
TITLE_A = ["Silent", "Hidden", "Northern", "Broken", "Golden", "Distant", "Iron", "Paper", "Winter", "Last"]
TITLE_B = ["River", "Garden", "Map", "Harbour", "Lantern", "Orchard", "Engine", "Archive", "Island", "Letter"]

DEPOTS = {"Leeds": "north", "Newcastle": "north", "Bristol": "west", "Cardiff": "west", "Norwich": "east",
          "Ipswich": "east", "Brighton": "south", "Southampton": "south"}
MODELS = ["Transit 350", "Sprinter 316", "Vivaro L2", "Master L3", "Ducato 35"]
PARTS = ["brake pads", "oil filter", "tyres", "battery", "wiper blades", "timing belt"]


def person(rng):
    return f"{rng.choice(FIRST)} {rng.choice(LAST)}"


def day(rng, year=2026):
    return f"{year}-{rng.randint(1, 9):02d}-{rng.randint(1, 28):02d}"


# ---------------------------------------------------------------- data


def build_orders(rng):
    orders = []
    for i in range(150):
        country = rng.choice(list(COUNTRIES))
        order = {
            "order_id": 70001 + i,
            "placed": day(rng),
            "status": rng.choice(STATUSES),
            "customer": {"name": person(rng), "tier": rng.choice(TIERS),
                         "address": {"city": rng.choice(COUNTRIES[country]), "country": country}},
            "lines": [],
        }
        for _ in range(rng.randint(1, 4)):
            line = {"sku": rng.choice(SKUS), "qty": rng.randint(1, 12), "unit_price": round(rng.uniform(3, 250), 2)}
            codes = rng.sample(DISCOUNTS, rng.choice([0, 0, 1, 1, 2]))
            line["discounts"] = [{"code": c, "pct": {"SPRING10": 10, "LOYAL5": 5, "BULK15": 15, "WELCOME": 20}[c]} for c in codes]
            order["lines"].append(line)
        tags = rng.sample(ORDER_TAGS, rng.choice([0, 1, 1, 2, 3]))
        if tags:
            order["tags"] = tags
        orders.append(order)
    return orders


def build_readings(rng):
    readings = []
    for i in range(220):
        site = rng.choice(list(SITES))
        rows = rng.randint(2, 3)
        cols = rng.randint(2, 3)
        reading = {
            "reading_id": f"R-{i + 1:04d}",
            "device": f"dev-{rng.randint(1, 24):02d}",
            "site": {"name": site, "region": SITES[site]},
            "taken": f"{day(rng)}T{rng.randint(0, 23):02d}:{rng.choice([0, 15, 30, 45]):02d}:00Z",
            "metrics": {"temp_c": round(rng.uniform(-5, 38), 1), "humidity": rng.randint(15, 95)},
            "alerts": [{"level": rng.choice(["warn", "warn", "critical"]), "code": rng.choice(ALERT_CODES)}
                       for _ in range(rng.choice([0, 0, 0, 1, 1, 2]))],
            "labels": rng.sample(LABELS, rng.choice([0, 1, 1, 2])),
            "calibration": [[round(rng.uniform(0.5, 1.5), 3) for _ in range(cols)] for _ in range(rows)],
        }
        readings.append(reading)
    return readings


def build_library(rng):
    branches = []
    isbn = 9780000100
    for name, city in BRANCHES:
        books = []
        for _ in range(30):
            isbn += rng.randint(3, 40)
            books.append({
                "isbn": str(isbn),
                "year": rng.randint(1950, 2025),
                "title": f"The {rng.choice(TITLE_A)} {rng.choice(TITLE_B)}",
                "author": person(rng),
                "genre": rng.choice(GENRES),
                "loans": [{"member": f"M-{rng.randint(100, 999)}", "days": rng.choice([7, 14, 21, 28])}
                          for _ in range(rng.choice([0, 1, 2, 2, 3, 4]))],
                "keywords": rng.sample(KEYWORDS, rng.choice([0, 1, 1, 2])),
            })
        branches.append({"name": name, "city": city, "books": books})
    return branches


def build_fleet(rng):
    vehicles = []
    for i in range(60):
        city = rng.choice(list(DEPOTS))
        vehicle = {
            "id": f"V-{101 + i}",
            "model": rng.choice(MODELS),
            "year": rng.randint(2015, 2025),
            "depot": {"city": city, "region": DEPOTS[city]},
            "services": [],
            "drivers": sorted({person(rng) for _ in range(rng.randint(1, 3))}),
        }
        odometer = rng.randint(5000, 40000)
        for _ in range(rng.randint(1, 5)):
            odometer += rng.randint(4000, 20000)
            vehicle["services"].append({"date": day(rng), "odometer": odometer, "cost": round(rng.uniform(80, 900), 2),
                                        "parts": rng.sample(PARTS, rng.randint(1, 3))})
        vehicles.append(vehicle)
    # A few drivers share vans, so "which vans does X drive" has more than one answer.
    for a, b in [(3, 17), (8, 41), (22, 55), (30, 12)]:
        shared = vehicles[a]["drivers"][0]
        if shared not in vehicles[b]["drivers"]:
            vehicles[b]["drivers"] = sorted(vehicles[b]["drivers"] + [shared])
    return vehicles


# ---------------------------------------------------------------- files


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def write_xml(path, branches):
    out = ['<?xml version="1.0" encoding="UTF-8"?>', "<library>"]
    for b in branches:
        out.append(f"  <branch name={quoteattr(b['name'])} city={quoteattr(b['city'])}>")
        for book in b["books"]:
            out.append(f"    <book isbn=\"{book['isbn']}\" year=\"{book['year']}\">")
            out.append(f"      <title>{escape(book['title'])}</title>")
            out.append(f"      <author>{escape(book['author'])}</author>")
            out.append(f"      <genre>{book['genre']}</genre>")
            for k in book["keywords"]:
                out.append(f"      <keyword>{k}</keyword>")
            for loan in book["loans"]:
                out.append(f"      <loan member=\"{loan['member']}\" days=\"{loan['days']}\"/>")
            out.append("    </book>")
        out.append("  </branch>")
    out.append("</library>")
    write(path, "\n".join(out) + "\n")


def write_yaml(path, vehicles):
    out = []
    for v in vehicles:
        out.append(f"- id: {v['id']}")
        out.append(f"  model: {v['model']}")
        out.append(f"  year: {v['year']}")
        out.append("  depot:")
        out.append(f"    city: {v['depot']['city']}")
        out.append(f"    region: {v['depot']['region']}")
        out.append("  services:")
        for s in v["services"]:
            out.append(f"    - date: {s['date']}")
            out.append(f"      odometer: {s['odometer']}")
            out.append(f"      cost: {s['cost']}")
            out.append(f"      parts: [{', '.join(s['parts'])}]")
        out.append("  drivers:")
        for d in v["drivers"]:
            out.append(f"    - {d}")
    write(path, "\n".join(out) + "\n")


# ---------------------------------------------------------------- reference tables


def reference(orders, readings, branches, vehicles):
    con = sqlite3.connect(":memory:")
    c = con.cursor()
    c.executescript("""
        CREATE TABLE orders(order_id INTEGER, placed TEXT, status TEXT, cust_name TEXT, tier TEXT, city TEXT, country TEXT);
        CREATE TABLE lines(order_id INTEGER, line_no INTEGER, sku TEXT, qty INTEGER, unit_price REAL);
        CREATE TABLE discounts(order_id INTEGER, line_no INTEGER, code TEXT, pct INTEGER);
        CREATE TABLE tags(order_id INTEGER, tag TEXT);
        CREATE TABLE readings(reading_id TEXT, device TEXT, site TEXT, region TEXT, taken TEXT, temp_c REAL, humidity INTEGER, calibration TEXT);
        CREATE TABLE alerts(reading_id TEXT, level TEXT, code TEXT);
        CREATE TABLE labels(reading_id TEXT, label TEXT);
        CREATE TABLE branches(branch TEXT, city TEXT);
        CREATE TABLE books(isbn TEXT, branch TEXT, year INTEGER, title TEXT, author TEXT, genre TEXT);
        CREATE TABLE loans(isbn TEXT, member TEXT, days INTEGER);
        CREATE TABLE keywords(isbn TEXT, keyword TEXT);
        CREATE TABLE vehicles(id TEXT, model TEXT, year INTEGER, depot_city TEXT, depot_region TEXT);
        CREATE TABLE services(id TEXT, svc_no INTEGER, date TEXT, odometer INTEGER, cost REAL);
        CREATE TABLE parts(id TEXT, svc_no INTEGER, part TEXT);
        CREATE TABLE drivers(id TEXT, driver TEXT);
    """)
    for o in orders:
        cu = o["customer"]
        c.execute("INSERT INTO orders VALUES (?,?,?,?,?,?,?)", (o["order_id"], o["placed"], o["status"], cu["name"], cu["tier"],
                                                                 cu["address"]["city"], cu["address"]["country"]))
        for n, line in enumerate(o["lines"]):
            c.execute("INSERT INTO lines VALUES (?,?,?,?,?)", (o["order_id"], n, line["sku"], line["qty"], line["unit_price"]))
            for d in line["discounts"]:
                c.execute("INSERT INTO discounts VALUES (?,?,?,?)", (o["order_id"], n, d["code"], d["pct"]))
        for t in o.get("tags", []):
            c.execute("INSERT INTO tags VALUES (?,?)", (o["order_id"], t))
    for r in readings:
        c.execute("INSERT INTO readings VALUES (?,?,?,?,?,?,?,?)", (r["reading_id"], r["device"], r["site"]["name"], r["site"]["region"],
                                                                    r["taken"], r["metrics"]["temp_c"], r["metrics"]["humidity"],
                                                                    json.dumps(r["calibration"])))
        for a in r["alerts"]:
            c.execute("INSERT INTO alerts VALUES (?,?,?)", (r["reading_id"], a["level"], a["code"]))
        for lab in r["labels"]:
            c.execute("INSERT INTO labels VALUES (?,?)", (r["reading_id"], lab))
    for b in branches:
        c.execute("INSERT INTO branches VALUES (?,?)", (b["name"], b["city"]))
        for book in b["books"]:
            c.execute("INSERT INTO books VALUES (?,?,?,?,?,?)", (book["isbn"], b["name"], book["year"], book["title"], book["author"], book["genre"]))
            for loan in book["loans"]:
                c.execute("INSERT INTO loans VALUES (?,?,?)", (book["isbn"], loan["member"], loan["days"]))
            for k in book["keywords"]:
                c.execute("INSERT INTO keywords VALUES (?,?)", (book["isbn"], k))
    for v in vehicles:
        c.execute("INSERT INTO vehicles VALUES (?,?,?,?,?)", (v["id"], v["model"], v["year"], v["depot"]["city"], v["depot"]["region"]))
        for n, s in enumerate(v["services"]):
            c.execute("INSERT INTO services VALUES (?,?,?,?,?)", (v["id"], n, s["date"], s["odometer"], s["cost"]))
            for p in s["parts"]:
                c.execute("INSERT INTO parts VALUES (?,?,?)", (v["id"], n, p))
        for d in v["drivers"]:
            c.execute("INSERT INTO drivers VALUES (?,?)", (v["id"], d))
    con.commit()
    return con


# ---------------------------------------------------------------- questions


class Family:
    """Draws questions for one family from its templates in turn, keeping only those with a usable answer."""

    def __init__(self, name):
        self.name = name
        self.items = []
        self.seen = set()

    def add(self, con, template, question, answer_type, sql, params, file):
        if (template, question) in self.seen:
            return False
        rows = con.execute(sql, params).fetchall()
        if answer_type.startswith("list"):
            answer = sorted(r[0] for r in rows)
            if not 2 <= len(answer) <= 6:
                return False
        else:
            if len(rows) != 1 or rows[0][0] is None:
                return False
            answer = rows[0][0]
            if answer_type == "number":
                if answer == 0:
                    return False
                answer = round(answer, 4) if isinstance(answer, float) else answer
        self.seen.add((template, question))
        self.items.append({"family": self.name, "template": template, "question": question, "answer_type": answer_type,
                           "answer": answer, "gold_sql": inline(sql, params), "file": file})
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


def questions(con, rng, orders, readings, branches, vehicles):
    books = [book for b in branches for book in b["books"]]
    countries = list(COUNTRIES)
    regions = sorted(set(SITES.values()))
    depot_regions = sorted(set(DEPOTS.values()))
    out = []

    f = Family("lookup")
    fill(f, rng, [
        lambda r: (lambda o: f.add(con, "order_city", f"Which city is order {o['order_id']} being sent to?", "category",
                                   "SELECT city FROM orders WHERE order_id = ?", [o["order_id"]], "orders.json"))(r.choice(orders)),
        lambda r: (lambda x: f.add(con, "reading_region", f"Which region is the site of sensor reading {x['reading_id']} in?", "category",
                                   "SELECT region FROM readings WHERE reading_id = ?", [x["reading_id"]], "readings.jsonl"))(r.choice(readings)),
        lambda r: (lambda b: f.add(con, "book_author", f"Who wrote the library book with ISBN {b['isbn']}?", "category",
                                   "SELECT author FROM books WHERE isbn = ?", [b["isbn"]], "library.xml"))(r.choice(books)),
        lambda r: (lambda v: f.add(con, "van_depot", f"Which city is the depot of van {v['id']} in?", "category",
                                   "SELECT depot_city FROM vehicles WHERE id = ?", [v["id"]], "fleet.yaml"))(r.choice(vehicles)),
    ])
    out += f.items

    f = Family("filter_count")
    fill(f, rng, [
        lambda r: (lambda c, s: f.add(con, "orders_country_status", f"How many orders going to {c} have the status {s}?", "number",
                                      "SELECT COUNT(*) FROM orders WHERE country = ? AND status = ?", [c, s], "orders.json"))(r.choice(countries), r.choice(STATUSES)),
        lambda r: (lambda g, t: f.add(con, "readings_region_temp", f"How many sensor readings from sites in the {g} region recorded a temperature above {t} °C?", "number",
                                      "SELECT COUNT(*) FROM readings WHERE region = ? AND temp_c > ?", [g, t], "readings.jsonl"))(r.choice(regions), r.choice([15, 20, 25, 30])),
        lambda r: (lambda g, y: f.add(con, "books_genre_year", f"How many {g} books in the library were published in {y} or later?", "number",
                                      "SELECT COUNT(*) FROM books WHERE genre = ? AND year >= ?", [g, y], "library.xml"))(r.choice(GENRES), r.choice([1980, 1990, 2000, 2010])),
        lambda r: (lambda g, y: f.add(con, "vans_region_year", f"How many vans based at depots in the {g} region were built in {y} or later?", "number",
                                      "SELECT COUNT(*) FROM vehicles WHERE depot_region = ? AND year >= ?", [g, y], "fleet.yaml"))(r.choice(depot_regions), r.choice([2017, 2019, 2021])),
    ])
    out += f.items

    f = Family("child_aggregate")
    fill(f, rng, [
        lambda r: (lambda s, c: f.add(con, "sku_qty_country", f"What is the total quantity of {s} ordered across all orders going to {c}?", "number",
                                      "SELECT SUM(l.qty) FROM lines l JOIN orders o ON o.order_id = l.order_id WHERE l.sku = ? AND o.country = ?", [s, c], "orders.json"))(r.choice(SKUS), r.choice(countries)),
        lambda r: (lambda s, lv: f.add(con, "alerts_site_level", f"How many {lv} alerts were raised in readings taken at the {s} site?", "number",
                                       "SELECT COUNT(*) FROM alerts a JOIN readings x ON x.reading_id = a.reading_id WHERE x.site = ? AND a.level = ?", [s, lv], "readings.jsonl"))(r.choice(list(SITES)), r.choice(["warn", "critical"])),
        lambda r: (lambda g: f.add(con, "loan_days_genre", f"What is the total number of loan days across all loans of {g} books?", "number",
                                   "SELECT SUM(l.days) FROM loans l JOIN books b ON b.isbn = l.isbn WHERE b.genre = ?", [g], "library.xml"))(r.choice(GENRES)),
        lambda r: (lambda c: f.add(con, "service_cost_depot", f"What is the total cost of all services of vans based at the {c} depot?", "number",
                                   "SELECT SUM(s.cost) FROM services s JOIN vehicles v ON v.id = s.id WHERE v.depot_city = ?", [c], "fleet.yaml"))(r.choice(list(DEPOTS))),
    ])
    out += f.items

    f = Family("grandchild")
    fill(f, rng, [
        lambda r: (lambda d, t: f.add(con, "discount_tier", f"How many times was the discount code {d} applied to order lines in orders from {t}-tier customers?", "number",
                                      "SELECT COUNT(*) FROM discounts d JOIN orders o ON o.order_id = d.order_id WHERE d.code = ? AND o.tier = ?", [d, t], "orders.json"))(r.choice(DISCOUNTS), r.choice(TIERS)),
        lambda r: (lambda d, c: f.add(con, "discount_country_orders", f"How many different orders going to {c} have at least one line with the discount code {d}?", "number",
                                      "SELECT COUNT(DISTINCT d.order_id) FROM discounts d JOIN orders o ON o.order_id = d.order_id WHERE d.code = ? AND o.country = ?", [d, c], "orders.json"))(r.choice(DISCOUNTS), r.choice(countries)),
        lambda r: (lambda b, n: f.add(con, "loans_branch_days", f"How many loans of {n} days or longer were made of books held at the {b} branch?", "number",
                                      "SELECT COUNT(*) FROM loans l JOIN books k ON k.isbn = l.isbn WHERE k.branch = ? AND l.days >= ?", [b, n], "library.xml"))(r.choice([x[0] for x in BRANCHES]), r.choice([14, 21, 28])),
        lambda r: (lambda p, g: f.add(con, "parts_region", f"How many times was the part \"{p}\" replaced in services of vans based at depots in the {g} region?", "number",
                                      "SELECT COUNT(*) FROM parts p JOIN vehicles v ON v.id = p.id WHERE p.part = ? AND v.depot_region = ?", [p, g], "fleet.yaml"))(r.choice(PARTS), r.choice(depot_regions)),
    ])
    out += f.items

    f = Family("scalar_list")
    fill(f, rng, [
        lambda r: (lambda t, s: f.add(con, "tag_status", f"How many orders with the status {s} are tagged {t}?", "number",
                                      "SELECT COUNT(*) FROM tags t JOIN orders o ON o.order_id = t.order_id WHERE t.tag = ? AND o.status = ?", [t, s], "orders.json"))(r.choice(ORDER_TAGS), r.choice(STATUSES)),
        lambda r: (lambda lab, g: f.add(con, "label_region", f"How many sensor readings from sites in the {g} region carry the label {lab}?", "number",
                                        "SELECT COUNT(*) FROM labels l JOIN readings x ON x.reading_id = l.reading_id WHERE l.label = ? AND x.region = ?", [lab, g], "readings.jsonl"))(r.choice(LABELS), r.choice(regions)),
        lambda r: (lambda k, b: f.add(con, "keyword_branch", f"How many books at the {b} branch have the keyword {k}?", "number",
                                      "SELECT COUNT(*) FROM keywords k JOIN books b ON b.isbn = k.isbn WHERE k.keyword = ? AND b.branch = ?", [k, b], "library.xml"))(r.choice(KEYWORDS), r.choice([x[0] for x in BRANCHES])),
        lambda r: (lambda d: f.add(con, "driver_vans", f"Which vans does {d} drive? Give their ids.", "list[category]",
                                   "SELECT id FROM drivers WHERE driver = ?", [d], "fleet.yaml"))(r.choice([v["drivers"][0] for v in vehicles])),
    ])
    out += f.items

    f = Family("json")
    fill(f, rng, [
        lambda r: (lambda x, i, j: f.add(con, "calibration_cell", f"In the calibration matrix of sensor reading {x['reading_id']}, what is the value in row {i + 1}, column {j + 1}?", "number",
                                         f"SELECT json_extract(calibration, '$[{i}][{j}]') FROM readings WHERE reading_id = ?", [x["reading_id"]], "readings.jsonl"))(*cell(r, readings)),
        lambda r: (lambda x: f.add(con, "calibration_sum", f"What is the sum of every value in the calibration matrix of sensor reading {x['reading_id']}?", "number",
                                   "SELECT SUM(e.value) FROM readings, json_each(calibration) AS row_, json_each(row_.value) AS e WHERE reading_id = ?", [x["reading_id"]], "readings.jsonl"))(r.choice(readings)),
        lambda r: (lambda x: f.add(con, "calibration_rows", f"How many rows does the calibration matrix of sensor reading {x['reading_id']} have?", "number",
                                   "SELECT json_array_length(calibration) FROM readings WHERE reading_id = ?", [x["reading_id"]], "readings.jsonl"))(r.choice(readings)),
    ])
    out += f.items
    return out


def cell(rng, readings):
    x = rng.choice(readings)
    i = rng.randrange(len(x["calibration"]))
    return x, i, rng.randrange(len(x["calibration"][i]))


def main():
    rng = random.Random(SEED)
    orders = build_orders(rng)
    readings = build_readings(rng)
    branches = build_library(rng)
    vehicles = build_fleet(rng)

    write(os.path.join(DOCS_DIR, "orders.json"), json.dumps(orders, indent=2) + "\n")
    write(os.path.join(DOCS_DIR, "readings.jsonl"), "".join(json.dumps(r) + "\n" for r in readings))
    write_xml(os.path.join(DOCS_DIR, "library.xml"), branches)
    write_yaml(os.path.join(DOCS_DIR, "fleet.yaml"), vehicles)

    con = reference(orders, readings, branches, vehicles)
    items = questions(con, random.Random(SEED + 1), orders, readings, branches, vehicles)
    lines = []
    counters = {"tune": 0, "test": 0}
    by_family = {}
    for q in items:
        by_family.setdefault(q["family"], []).append(q)
    for family, qs in by_family.items():
        for n, q in enumerate(qs):
            split = "tune" if n < TUNE_PER_FAMILY else "test"
            counters[split] += 1
            lines.append({"id": f"nested-{split}-{counters[split]:03d}", "split": split, **q})
    lines.sort(key=lambda q: (q["split"] != "tune", q["id"]))
    write(os.path.join(NESTED_DIR, "questions.jsonl"), "".join(json.dumps(q, ensure_ascii=False) + "\n" for q in lines))
    print(f"{len(lines)} questions: {counters['tune']} tune, {counters['test']} test")


if __name__ == "__main__":
    main()
