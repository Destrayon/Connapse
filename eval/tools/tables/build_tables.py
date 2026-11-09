"""Builds the tables-v1 CSVs (CON-298, CON-E6 ticket 1).

Four tables come from DataBench (Osés Grijalba et al., LREC-COLING 2024; huggingface.co/datasets/cardiffnlp/databench,
MIT) at a pinned revision, each source file checked against its SHA-256. The rest describe a fictional company and
are generated from a seeded random.Random, so the same script writes the same bytes on any machine:

    customers, products, orders (100,000 rows), invoices, employees,
    tickets split into three files by queue (support, billing, security; the restricted user cannot read security),
    inventory written twice (load1, load2): the same document re-uploaded with changed counts.

Usage:
    python eval/tools/tables/build_tables.py [--databench-dir DIR]

Output: eval/.cache/datasets/tables/v1/*.csv and build.json (rows and SHA-256 of each file). The parquet sources are
downloaded to eval/.cache/sources/databench/<revision>/ unless --databench-dir holds them already.
"""
import argparse
import datetime as dt
import hashlib
import json
import os
import random
import sys

import pandas as pd
import requests

sys.path.insert(0, os.path.dirname(__file__))
from common import CACHE_DIR, ROOT, write_csv  # noqa: E402

DATABENCH_REVISION = "e75d53add267d2f9cfa32efd65ad77f0807adfad"
DATABENCH = {
    # DataBench id: (output file, parquet SHA-256, columns dropped)
    "001_Forbes": ("forbes.csv", "1c825f7fc6c69ee342518bcfc4937ca6828028c377ae1ea81b608e709db4443d", ["bio", "about"]),
    "027_Supermarket": ("supermarket.csv", "230a68420b15e77afc13b768a498af0f3b0f8a2823956cdab1007116a29ca5b5", []),
    "076_NBA": ("nba.csv", "326c148a50d90a37b4518c96161f8b069c1a0b52f578fe1f7f085cbd92e31055", []),
    "047_Bank": ("bank.csv", "b03efb8076b77af3ed8b933a74a00dc67459abfdf1ab0c94e5a7d675de781dcd", []),
}
SEED = 20261108


# ---------------------------------------------------------------- DataBench

def fetch_databench(databench_dir):
    paths = {}
    for name, (_, digest, _) in DATABENCH.items():
        local = os.path.join(databench_dir, name, "all.parquet") if databench_dir else None
        if not local or not os.path.exists(local):
            local = os.path.join(ROOT, "eval", ".cache", "sources", "databench", DATABENCH_REVISION, name, "all.parquet")
        if not os.path.exists(local):
            url = f"https://huggingface.co/datasets/cardiffnlp/databench/resolve/{DATABENCH_REVISION}/data/{name}/all.parquet"
            os.makedirs(os.path.dirname(local), exist_ok=True)
            with requests.get(url, timeout=120) as r:
                r.raise_for_status()
                with open(local, "wb") as f:
                    f.write(r.content)
        with open(local, "rb") as f:
            actual = hashlib.sha256(f.read()).hexdigest()
        if actual != digest:
            raise SystemExit(f"{local}: sha256 {actual}, expected {digest}")
        paths[name] = local
    return paths


def databench_rows(path, dropped):
    frame = pd.read_parquet(path).drop(columns=dropped)
    columns = []
    for column in frame.columns:
        series = frame[column]
        if pd.api.types.is_datetime64_any_dtype(series):
            present = series.dropna()
            midnight = bool((present.dt.hour == 0).all() and (present.dt.minute == 0).all() and (present.dt.second == 0).all())
            pattern = "%Y-%m-%d" if midnight else "%Y-%m-%dT%H:%M:%SZ"
            columns.append([None if pd.isna(v) else v.strftime(pattern) for v in series])
        elif pd.api.types.is_bool_dtype(series):
            columns.append([None if pd.isna(v) else bool(v) for v in series])
        elif pd.api.types.is_integer_dtype(series):
            columns.append([None if pd.isna(v) else int(v) for v in series])
        elif pd.api.types.is_float_dtype(series):
            columns.append([None if pd.isna(v) else float(v) for v in series])
        else:
            # DataBench stores NBA's season type URL-encoded ("Regular%20Season"); the CSV decodes it.
            columns.append([None if pd.isna(v) else str(v).replace("%20", " ") for v in series])
    return list(frame.columns), list(zip(*columns))


# ---------------------------------------------------------------- the fictional company

REGIONS = {
    "North America": {"United States": ["Austin", "Chicago", "Denver", "Seattle", "Boston"], "Canada": ["Toronto", "Vancouver"]},
    "EMEA": {"United Kingdom": ["London", "Manchester"], "Germany": ["Berlin", "Munich"], "France": ["Paris", "Lyon"]},
    "APAC": {"Japan": ["Tokyo", "Osaka"], "Australia": ["Sydney", "Melbourne"], "Singapore": ["Singapore"]},
    "LATAM": {"Brazil": ["Sao Paulo"], "Mexico": ["Mexico City", "Monterrey"]},
}
OFFICES = ["Austin", "London", "Singapore", "Toronto", "Sao Paulo"]
FIRST = ["Ada", "Ben", "Chloe", "Dev", "Elena", "Farid", "Grace", "Hiro", "Ines", "Jonah", "Kira", "Liam", "Maya", "Nikhil",
         "Olga", "Pablo", "Quinn", "Rosa", "Sami", "Tara", "Umar", "Vera", "Wes", "Xin", "Yara", "Zoe", "Aiden", "Bea",
         "Caleb", "Dana", "Emil", "Fiona", "Gus", "Hana", "Ivan", "Jade", "Kofi", "Lena", "Marco", "Nora"]
LAST = ["Abbott", "Barros", "Chen", "Dubois", "Eriksen", "Fischer", "Garcia", "Hale", "Ito", "Jensen", "Kowalski", "Lopez",
        "Moreau", "Nakamura", "Okafor", "Patel", "Quist", "Rossi", "Silva", "Tanaka", "Ueda", "Varga", "Weber", "Xu",
        "Yilmaz", "Zimmer", "Andersen", "Bianchi", "Costa", "Diaz", "Engel", "Ferreira", "Gruber", "Haas", "Iqbal",
        "Kaur", "Lindqvist", "Mendes", "Novak", "Ortega"]
COMPANY_A = ["Apex", "Blue", "Cedar", "Delta", "Ember", "Falcon", "Granite", "Harbor", "Iron", "Juniper", "Keystone",
             "Lumen", "Maple", "Nimbus", "Orbit", "Pioneer", "Quartz", "River", "Summit", "Tidal", "Union", "Vertex",
             "Willow", "Zenith", "Aurora", "Beacon", "Cobalt", "Drift", "Echo", "Frontier"]
COMPANY_B = ["Analytics", "Logistics", "Foods", "Health", "Energy", "Labs", "Retail", "Systems", "Media", "Finance",
             "Motors", "Textiles", "Robotics", "Pharma", "Telecom", "Studios", "Freight", "Insurance", "Mining", "Water"]
COMPANY_C = ["Inc", "LLC", "Ltd", "GmbH", "Group", "Co", "Partners", "Holdings"]
DEPARTMENTS = {
    "Sales": ["Account Executive", "Account Manager", "Sales Director"],
    "Support": ["Support Agent", "Senior Support Agent", "Support Lead"],
    "Engineering": ["Software Engineer", "Senior Engineer", "Staff Engineer", "Engineering Manager"],
    "Finance": ["Accountant", "Financial Analyst", "Controller"],
    "Marketing": ["Marketing Specialist", "Content Lead", "Marketing Manager"],
    "Operations": ["Warehouse Associate", "Logistics Coordinator", "Operations Manager"],
    "Security": ["Security Analyst", "Security Engineer", "CISO"],
}
DEPARTMENT_WEIGHTS = {"Sales": 18, "Support": 22, "Engineering": 25, "Finance": 7, "Marketing": 8, "Operations": 15, "Security": 5}
SALARY_BASE = {"Sales": 70000, "Support": 52000, "Engineering": 110000, "Finance": 80000, "Marketing": 68000,
               "Operations": 50000, "Security": 105000}
CATEGORIES = {"Hardware": (80, 2500), "Software": (20, 900), "Services": (150, 5000), "Accessories": (5, 120), "Training": (200, 1800)}
PRODUCT_A = ["Nova", "Atlas", "Pulse", "Vector", "Helix", "Prism", "Strata", "Flux", "Orion", "Terra", "Lyra", "Quanta"]
PRODUCT_B = {"Hardware": ["Router", "Switch", "Server", "Sensor", "Gateway", "Tablet"],
             "Software": ["Suite", "Cloud", "Insight", "Guard", "Sync", "Studio"],
             "Services": ["Install", "Audit", "Migration", "Support Plan", "Consulting", "Onboarding"],
             "Accessories": ["Cable", "Mount", "Adapter", "Battery", "Case", "Dock"],
             "Training": ["Bootcamp", "Workshop", "Certification", "Course", "Seminar", "Lab"]}
SUPPLIERS = ["Northwind Components", "Contoso Supply", "Fabrikam Parts", "Tailspin Goods", "Wingtip Distribution", "Litware Media"]
WAREHOUSES = ["Reno", "Rotterdam", "Singapore", "Toronto"]


def unique_names(rng, count, make):
    seen = set()
    out = []
    while len(out) < count:
        name = make(rng)
        if name not in seen:
            seen.add(name)
            out.append(name)
    return out


def money(x):
    return round(x + 1e-9, 2)


def day(start, end, rng):
    return start + dt.timedelta(days=rng.randrange((end - start).days + 1))


def employees(rng):
    names = unique_names(rng, 1500, lambda r: f"{r.choice(FIRST)} {r.choice(LAST)} {r.choice('ABCDEFGHJKLMNPRSTW')}.")
    departments = list(DEPARTMENT_WEIGHTS)
    weights = list(DEPARTMENT_WEIGHTS.values())
    rows = []
    heads = {}
    for i, name in enumerate(names):
        employee_id = 1001 + i
        department = departments[i] if i < len(departments) else rng.choices(departments, weights)[0]
        titles = DEPARTMENTS[department]
        title = titles[-1] if i < len(departments) else rng.choice(titles[:-1])
        hire = day(dt.date(2012, 1, 1), dt.date(2025, 6, 30), rng)
        if i < len(departments):
            heads[department] = employee_id
            manager = None
        else:
            manager = heads[department]
        level = titles.index(title)
        salary = int(round(SALARY_BASE[department] * (1 + 0.25 * level) * rng.uniform(0.85, 1.2), -2))
        active = rng.random() > 0.08
        rows.append([employee_id, name, department, title, hire.isoformat(), rng.choice(OFFICES), salary, manager, active])
    header = ["employee_id", "full_name", "department", "title", "hire_date", "office", "salary", "manager_id", "active"]
    return header, rows


def customers(rng, account_managers):
    names = unique_names(rng, 5000, lambda r: f"{r.choice(COMPANY_A)} {r.choice(COMPANY_B)} {r.choice(COMPANY_C)}"
                                              + ("" if r.random() < 0.55 else f" {r.randint(2, 99)}"))
    rows = []
    for i, name in enumerate(names):
        region = rng.choices(list(REGIONS), [45, 30, 17, 8])[0]
        country = rng.choice(list(REGIONS[region]))
        city = rng.choice(REGIONS[region][country])
        segment = rng.choices(["Enterprise", "Mid-Market", "SMB"], [15, 35, 50])[0]
        limit = {"Enterprise": 250000, "Mid-Market": 60000, "SMB": 15000}[segment] * rng.choice([0.5, 1, 1, 2, 4])
        rows.append([10001 + i, name, segment, region, country, city, day(dt.date(2016, 1, 1), dt.date(2025, 9, 30), rng).isoformat(),
                     rng.choice(account_managers), int(limit)])
    header = ["customer_id", "name", "segment", "region", "country", "city", "signup_date", "account_manager_id", "credit_limit"]
    return header, rows


def products(rng):
    rows = []
    names = set()
    i = 0
    while len(rows) < 1200:
        category = rng.choices(list(CATEGORIES), [30, 25, 15, 20, 10])[0]
        name = f"{rng.choice(PRODUCT_A)} {rng.choice(PRODUCT_B[category])} {rng.choice(['', 'Pro ', 'Lite ', 'Max ', 'Plus '])}{rng.randint(1, 60)}0"
        name = " ".join(name.split())
        if name in names:
            continue
        names.add(name)
        low, high = CATEGORIES[category]
        price = money(rng.uniform(low, high))
        cost = money(price * rng.uniform(0.35, 0.8))
        launched = day(dt.date(2018, 1, 1), dt.date(2025, 6, 30), rng)
        discontinued = launched < dt.date(2022, 1, 1) and rng.random() < 0.3
        rows.append([2001 + i, name, category, price, cost, rng.choice(SUPPLIERS), launched.isoformat(), discontinued])
        i += 1
    header = ["product_id", "name", "category", "unit_price", "cost", "supplier", "launched", "discontinued"]
    return header, rows


def orders(rng, customer_rows, product_rows):
    weights = [{"Enterprise": 8, "Mid-Market": 3, "SMB": 1}[c[2]] * rng.uniform(0.5, 1.5) for c in customer_rows]
    customer_ids = [c[0] for c in customer_rows]
    chosen = rng.choices(customer_ids, weights, k=100000)
    rows = []
    for i, customer_id in enumerate(chosen):
        product = rng.choice(product_rows)
        when = day(dt.date(2023, 1, 1), dt.date(2025, 12, 31), rng)
        price = money(product[3] * (1.04 ** (when.year - 2023)))
        quantity = rng.choices([1, 2, 3, 5, 10, 20, 50], [30, 20, 15, 15, 10, 7, 3])[0]
        discount = rng.choices([0, 5, 10, 15, 20], [50, 20, 15, 10, 5])[0]
        status = rng.choices(["completed", "shipped", "cancelled", "returned"], [80, 8, 7, 5])[0]
        channel = rng.choices(["web", "direct", "partner"], [50, 30, 20])[0]
        rows.append([500001 + i, customer_id, product[0], when.isoformat(), quantity, price, discount,
                     money(quantity * price * (100 - discount) / 100), status, channel])
    header = ["order_id", "customer_id", "product_id", "order_date", "quantity", "unit_price", "discount_pct", "total", "status", "channel"]
    return header, rows


def tickets(rng, customer_ids, agents_by_queue):
    queues = rng.choices(["support", "billing", "security"], [60, 25, 15], k=20000)
    categories = {"support": ["login", "performance", "bug", "how-to", "integration"],
                  "billing": ["refund", "invoice error", "payment failed", "plan change"],
                  "security": ["phishing report", "access review", "vulnerability", "data request"]}
    out = {"support": [], "billing": [], "security": []}
    for i, queue in enumerate(queues):
        opened = dt.datetime(2024, 1, 1) + dt.timedelta(minutes=rng.randrange(2 * 365 * 24 * 60))
        priority = rng.choices(["low", "medium", "high", "urgent"], [35, 40, 18, 7] if queue != "security" else [10, 30, 35, 25])[0]
        status = "open" if opened > dt.datetime(2025, 11, 1) and rng.random() < 0.6 else rng.choices(["closed", "open", "pending"], [88, 7, 5])[0]
        closed = None
        satisfaction = None
        if status == "closed":
            hours = rng.expovariate(1 / {"urgent": 10, "high": 30, "medium": 60, "low": 120}[priority])
            closed = (opened + dt.timedelta(minutes=max(5, int(hours * 60)))).isoformat(timespec="seconds")
            satisfaction = rng.choices([1, 2, 3, 4, 5, None], [5, 8, 17, 30, 25, 15])[0]
        out[queue].append([700001 + i, rng.choice(customer_ids), opened.isoformat(timespec="seconds"), closed, priority,
                           rng.choice(categories[queue]), status, rng.choice(agents_by_queue[queue]), satisfaction, queue])
    header = ["ticket_id", "customer_id", "opened_at", "closed_at", "priority", "category", "status", "agent_id", "satisfaction", "queue"]
    return header, out


def invoices(rng, customer_rows):
    rows = []
    for i in range(12000):
        customer = rng.choice(customer_rows)
        issued = day(dt.date(2023, 1, 1), dt.date(2025, 12, 31), rng)
        due = issued + dt.timedelta(days=rng.choice([15, 30, 30, 45, 60]))
        currency = {"EMEA": rng.choice(["EUR", "EUR", "GBP"])}.get(customer[3], "USD")
        if customer[4] == "United Kingdom":
            currency = "GBP"
        amount = money(rng.lognormvariate(8, 1.1))
        roll = rng.random()
        paid = None
        if due < dt.date(2025, 12, 1) and roll < 0.9 or roll < 0.4:
            paid = issued + dt.timedelta(days=int(rng.triangular(0, (due - issued).days + 40, (due - issued).days - 3)))
            paid = min(paid, dt.date(2025, 12, 31))
        status = "paid" if paid else ("overdue" if due < dt.date(2026, 1, 1) and roll > 0.5 else "open")
        rows.append([f"INV-{i + 1:06d}", customer[0], issued.isoformat(), due.isoformat(), amount,
                     paid.isoformat() if paid else None, status, currency])
    header = ["invoice_id", "customer_id", "issued_on", "due_on", "amount", "paid_on", "status", "currency"]
    return header, rows


def inventory(rng, product_rows):
    stocked = [p for p in product_rows if p[2] in ("Hardware", "Accessories")]
    load1 = []
    n = 0
    for product in stocked:
        for warehouse in WAREHOUSES:
            if rng.random() < 0.75:
                n += 1
                on_hand = rng.choice([0, 0, rng.randint(1, 20), rng.randint(20, 400), rng.randint(100, 2000)])
                load1.append([f"SKU-{n:05d}", product[0], warehouse, on_hand, rng.choice([10, 25, 50, 100]),
                              money(product[4] * rng.uniform(0.95, 1.05)), day(dt.date(2025, 6, 1), dt.date(2025, 9, 30), rng).isoformat()])
    load2 = []
    for row in load1:
        roll = rng.random()
        if roll < 0.03:
            continue  # SKU retired before the second count
        row = list(row)
        if roll < 0.45:
            row[3] = max(0, row[3] + rng.randint(-150, 150))
            row[6] = day(dt.date(2025, 10, 1), dt.date(2025, 12, 15), rng).isoformat()
        load2.append(row)
    for product in rng.sample(stocked, 40):
        n += 1
        load2.append([f"SKU-{n:05d}", product[0], rng.choice(WAREHOUSES), rng.randint(10, 600), 25,
                      money(product[4]), day(dt.date(2025, 10, 1), dt.date(2025, 12, 15), rng).isoformat()])
    header = ["sku", "product_id", "warehouse", "on_hand", "reorder_point", "unit_cost", "last_counted"]
    return header, load1, load2


def build(databench_dir):
    os.makedirs(CACHE_DIR, exist_ok=True)
    files = {}

    def emit(name, header, rows):
        files[name] = {"rows": len(rows), "sha256": write_csv(os.path.join(CACHE_DIR, name), header, rows)}

    for name, path in fetch_databench(databench_dir).items():
        out, _, dropped = DATABENCH[name]
        emit(out, *databench_rows(path, dropped))

    rng = random.Random(SEED)
    e_header, e_rows = employees(rng)
    emit("employees.csv", e_header, e_rows)
    by_title = {}
    for row in e_rows:
        by_title.setdefault(row[3], []).append(row[0])
    c_header, c_rows = customers(rng, by_title["Account Manager"] + by_title["Account Executive"])
    emit("customers.csv", c_header, c_rows)
    p_header, p_rows = products(rng)
    emit("products.csv", p_header, p_rows)
    emit("orders.csv", *orders(rng, c_rows, p_rows))
    support_agents = by_title["Support Agent"] + by_title["Senior Support Agent"]
    t_header, t_files = tickets(rng, [c[0] for c in c_rows], {
        "support": support_agents, "billing": by_title["Accountant"] + by_title["Financial Analyst"],
        "security": by_title["Security Analyst"] + by_title["Security Engineer"]})
    for queue, rows in t_files.items():
        emit(f"tickets-{queue}.csv", t_header, rows)
    emit("invoices.csv", *invoices(rng, c_rows))
    i_header, load1, load2 = inventory(rng, p_rows)
    emit("inventory.load1.csv", i_header, load1)
    emit("inventory.load2.csv", i_header, load2)

    manifest = {"databench_revision": DATABENCH_REVISION, "seed": SEED, "files": dict(sorted(files.items()))}
    with open(os.path.join(CACHE_DIR, "build.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    # The eval harness's "built locally" message names the dataset after the script; it is the only one.
    parser.add_argument("dataset", nargs="?", choices=["tables"], default="tables", help="the dataset to build")
    parser.add_argument("--databench-dir", help="folder holding <id>/all.parquet copies of the DataBench files")
    args = parser.parse_args()
    for name, info in build(args.databench_dir)["files"].items():
        print(f"{name:24} {info['rows']:>7} {info['sha256']}")
