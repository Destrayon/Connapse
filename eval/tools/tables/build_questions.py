"""Generates the tables-v1 questions (CON-298, CON-E6 ticket 1) from the CSVs that build_tables.py writes.

Every answer comes from running the question's gold SQL (SQLite dialect) over the CSVs, never from arithmetic done by
hand, and the SQL is kept with the question. Six families, 100 questions each: look up one value, filter and count,
sum or average, group and top-k, join two tables, and value as Connapse saw it on a date. Each family's questions are
dealt in blocks of ten (3 test, 3 reserve-1, 3 reserve-2, 1 tune), giving 60 tune + 180 test and two reserve sets of
180. The 30 mixed questions need a policy document and a table together; the policies are written by this script and
their numbers parameterize the gold SQL.

Checks that fail the build: a scalar query that does not return exactly one row, a top-k whose cut falls on a tie, a
restricted-user answer the grader can't tell from the admin answer, a seen-as-of answer the grader can't tell from
the other load's, and any repeated question text.

Usage:
    python eval/tools/tables/build_tables.py
    python eval/tools/tables/build_questions.py

Output, committed: eval/datasets/tables/questions.jsonl (tune + test), mixed.jsonl, docs/*.md, and the sealed
reserve-1.jsonl.gz and reserve-2.jsonl.gz. Do not open the reserve files; they are for re-tests only.
"""
import calendar
import gzip
import io
import json
import os
import random
import sys

sys.path.insert(0, os.path.dirname(__file__))
from common import CACHE_DIR, REPO_DIR, connect, load_table  # noqa: E402

SEED = 20261109
PER_FAMILY = 100
BLOCK = ["test", "test", "test", "reserve1", "reserve1", "reserve1", "reserve2", "reserve2", "reserve2", "tune"]

TABLES = {
    "forbes": ["forbes.csv"],
    "supermarket": ["supermarket.csv"],
    "nba": ["nba.csv"],
    "bank": ["bank.csv"],
    "customers": ["customers.csv"],
    "products": ["products.csv"],
    "orders": ["orders.csv"],
    "employees": ["employees.csv"],
    "invoices": ["invoices.csv"],
    "tickets": ["tickets-support.csv", "tickets-billing.csv", "tickets-security.csv"],
    "inventory": ["inventory.load2.csv"],
}
RESTRICTED_HIDDEN = "tickets-security.csv"


def open_databases():
    """admin: everything, current load; restricted: without the security tickets; load1: inventory's first load."""
    dbs = {}
    for name, hidden, inventory in (("admin", None, "inventory.load2.csv"),
                                    ("restricted", RESTRICTED_HIDDEN, "inventory.load2.csv"),
                                    ("load1", None, "inventory.load1.csv")):
        con = connect()
        for table, files in TABLES.items():
            files = [inventory] if table == "inventory" else [f for f in files if f != hidden]
            load_table(con, table, [os.path.join(CACHE_DIR, f) for f in files])
        dbs[name] = con
    return dbs


def lit(value):
    if isinstance(value, str):
        return "'" + value.replace("'", "''") + "'"
    return str(value)


def month_name(ym):
    year, month = ym.split("-")
    return f"{calendar.month_name[int(month)]} {year}"


def column(con, sql):
    return [row[0] for row in con.execute(sql)]


class Pools:
    def __init__(self, dbs):
        a = dbs["admin"]
        self.months = [f"{y}-{m:02d}" for y in (2023, 2024, 2025) for m in range(1, 13)]
        self.ticket_months = [f"{y}-{m:02d}" for y in (2024, 2025) for m in range(1, 13)]
        self.years = [2023, 2024, 2025]
        self.customers = a.execute("SELECT customer_id, name, segment, region, country FROM customers ORDER BY customer_id").fetchall()
        self.countries = column(a, "SELECT DISTINCT country FROM customers ORDER BY 1")
        self.regions = column(a, "SELECT DISTINCT region FROM customers ORDER BY 1")
        self.employees = column(a, "SELECT full_name FROM employees ORDER BY employee_id")
        self.managers = column(a, "SELECT e.full_name FROM employees e WHERE e.employee_id IN "
                                  "(SELECT account_manager_id FROM customers) ORDER BY e.employee_id")
        self.departments = column(a, "SELECT DISTINCT department FROM employees ORDER BY 1")
        self.offices = column(a, "SELECT DISTINCT office FROM employees ORDER BY 1")
        self.products = column(a, "SELECT name FROM products ORDER BY product_id")
        self.stocked = column(a, "SELECT DISTINCT p.name FROM products p JOIN inventory i ON i.product_id = p.product_id ORDER BY 1")
        self.categories = column(a, "SELECT DISTINCT category FROM products ORDER BY 1")
        self.suppliers = column(a, "SELECT DISTINCT supplier FROM products ORDER BY 1")
        self.order_ids = column(a, "SELECT order_id FROM orders ORDER BY order_id")
        self.invoice_ids = column(a, "SELECT invoice_id FROM invoices ORDER BY invoice_id")
        self.ticket_ids = column(a, "SELECT ticket_id FROM tickets ORDER BY ticket_id")
        self.forbes_people = column(a, "SELECT personName FROM forbes GROUP BY personName HAVING COUNT(*) = 1 ORDER BY 1")
        self.forbes_categories = column(a, "SELECT category FROM forbes GROUP BY category HAVING COUNT(*) >= 40 ORDER BY 1")
        self.forbes_countries = column(a, "SELECT country FROM forbes GROUP BY country HAVING COUNT(*) >= 25 ORDER BY 1")
        self.nba_keys = a.execute("SELECT PLAYER, year, Season_type FROM nba GROUP BY PLAYER, year, Season_type "
                                  "HAVING COUNT(*) = 1 ORDER BY 1, 2, 3").fetchall()
        self.nba_years = column(a, "SELECT DISTINCT year FROM nba ORDER BY 1")
        self.nba_teams = column(a, "SELECT DISTINCT TEAM FROM nba ORDER BY 1")
        self.bank_ids = column(a, "SELECT id FROM bank WHERE credit_limit IS NOT NULL ORDER BY id")
        self.bank_income = column(a, "SELECT DISTINCT income_category FROM bank ORDER BY 1")
        self.bank_education = column(a, "SELECT DISTINCT education_level FROM bank ORDER BY 1")
        self.market_invoices = column(a, 'SELECT "Invoice ID" FROM supermarket ORDER BY 1')
        self.market_cities = column(a, "SELECT DISTINCT City FROM supermarket ORDER BY 1")
        self.market_lines = column(a, 'SELECT DISTINCT "Product line" FROM supermarket ORDER BY 1')
        self.market_payments = column(a, "SELECT DISTINCT Payment FROM supermarket ORDER BY 1")
        self.warehouses = column(a, "SELECT DISTINCT warehouse FROM inventory ORDER BY 1")
        l1 = dbs["load1"]
        self.load1 = {r[0]: r for r in l1.execute("SELECT sku, on_hand, last_counted FROM inventory")}
        self.load2 = {r[0]: r for r in a.execute("SELECT sku, on_hand, last_counted FROM inventory")}
        self.both_skus = sorted(set(self.load1) & set(self.load2))
        self.only_one_skus = sorted(set(self.load1) ^ set(self.load2))


# ------------------------------------------------------------------ templates
# Each returns a dict: q (text), sql, type, tables; optional topk (sql selects label, metric ordered best first),
# restrictable (tickets-based, so the restricted user's answer can differ), as_of (seen-as-of family), nonzero.

def lookup_templates(p):
    def order_total(r):
        oid = r.choice(p.order_ids)
        return dict(q=f"What was the total of order {oid}?", sql=f"SELECT total FROM orders WHERE order_id = {oid}",
                    type="number", tables=["orders"])

    def customer_city(r):
        c = r.choice(p.customers)
        return dict(q=f"Which city is the customer {c[1]} based in?",
                    sql=f"SELECT city FROM customers WHERE name = {lit(c[1])}", type="category", tables=["customers"])

    def employee_title(r):
        name = r.choice(p.employees)
        return dict(q=f"What is {name}'s job title?", sql=f"SELECT title FROM employees WHERE full_name = {lit(name)}",
                    type="category", tables=["employees"])

    def product_price(r):
        name = r.choice(p.products)
        return dict(q=f"What is the list unit price of the product {name}?",
                    sql=f"SELECT unit_price FROM products WHERE name = {lit(name)}", type="number", tables=["products"])

    def forbes_worth(r):
        name = r.choice(p.forbes_people)
        return dict(q=f"In the Forbes billionaires list, what is {name}'s final worth in millions of dollars?",
                    sql=f"SELECT finalWorth FROM forbes WHERE personName = {lit(name)}", type="number", tables=["forbes"])

    def nba_points(r):
        player, year, season = r.choice(p.nba_keys)
        return dict(q=f"How many points did {player} score in the {year} NBA {season.lower()}?",
                    sql=f"SELECT PTS FROM nba WHERE PLAYER = {lit(player)} AND year = {lit(year)} AND Season_type = {lit(season)}",
                    type="number", tables=["nba"])

    def bank_limit(r):
        bid = r.choice(p.bank_ids)
        return dict(q=f"What is the credit limit of bank customer {bid}?",
                    sql=f"SELECT credit_limit FROM bank WHERE id = {bid}", type="number", tables=["bank"])

    def market_payment(r):
        inv = r.choice(p.market_invoices)
        return dict(q=f"Which payment method was used for supermarket invoice {inv}?",
                    sql=f'SELECT Payment FROM supermarket WHERE "Invoice ID" = {lit(inv)}', type="category", tables=["supermarket"])

    def invoice_due(r):
        inv = r.choice(p.invoice_ids)
        return dict(q=f"What is the due date of invoice {inv}?",
                    sql=f"SELECT due_on FROM invoices WHERE invoice_id = {lit(inv)}", type="category", tables=["invoices"])

    def ticket_priority(r):
        tid = r.choice(p.ticket_ids)
        return dict(q=f"What priority does ticket {tid} have?",
                    sql=f"SELECT priority FROM tickets WHERE ticket_id = {tid}", type="category", tables=["tickets"])

    return [order_total, customer_city, employee_title, product_price, forbes_worth, nba_points, bank_limit,
            market_payment, invoice_due, ticket_priority]


def count_templates(p):
    def orders_status(r):
        status = r.choice(["completed", "shipped", "cancelled", "returned"])
        channel = r.choice(["web", "direct", "partner"])
        ym = r.choice(p.months)
        return dict(q=f"How many {status} orders were placed through the {channel} channel in {month_name(ym)}?",
                    sql=f"SELECT COUNT(*) FROM orders WHERE status = {lit(status)} AND channel = {lit(channel)} "
                        f"AND substr(order_date, 1, 7) = {lit(ym)}", type="number", tables=["orders"], nonzero=True)

    def customers_segment(r):
        segment = r.choice(["Enterprise", "Mid-Market", "SMB"])
        country = r.choice(p.countries)
        year = r.choice(range(2016, 2026))
        return dict(q=f"How many {segment} customers in {country} signed up in {year}?",
                    sql=f"SELECT COUNT(*) FROM customers WHERE segment = {lit(segment)} AND country = {lit(country)} "
                        f"AND substr(signup_date, 1, 4) = {lit(str(year))}", type="number", tables=["customers"], nonzero=True)

    def tickets_priority(r):
        priority = r.choice(["low", "medium", "high", "urgent"])
        ym = r.choice(p.ticket_months)
        return dict(q=f"How many {priority} priority tickets were opened in {month_name(ym)}?",
                    sql=f"SELECT COUNT(*) FROM tickets WHERE priority = {lit(priority)} AND substr(opened_at, 1, 7) = {lit(ym)}",
                    type="number", tables=["tickets"], restrictable=True, nonzero=True)

    def employees_active(r):
        department = r.choice(p.departments)
        office = r.choice(p.offices)
        return dict(q=f"How many active employees in {department} work at the {office} office?",
                    sql=f"SELECT COUNT(*) FROM employees WHERE active = 'true' AND department = {lit(department)} "
                        f"AND office = {lit(office)}", type="number", tables=["employees"], nonzero=True)

    def forbes_count(r):
        category = r.choice(p.forbes_categories)
        country = r.choice(p.forbes_countries)
        return dict(q=f"How many billionaires in the Forbes list's {category} category are from {country}?",
                    sql=f"SELECT COUNT(*) FROM forbes WHERE category = {lit(category)} AND country = {lit(country)}",
                    type="number", tables=["forbes"], nonzero=True)

    def nba_games(r):
        year = r.choice(p.nba_years)
        games = r.choice([20, 40, 60, 70, 75])
        team = r.choice(p.nba_teams)
        return dict(q=f"How many {team} players played at least {games} games in the {year} NBA regular season?",
                    sql=f"SELECT COUNT(*) FROM nba WHERE year = {lit(year)} AND Season_type = 'Regular Season' "
                        f"AND TEAM = {lit(team)} AND GP >= {games}", type="number", tables=["nba"], nonzero=True)

    def bank_income(r):
        income = r.choice(p.bank_income)
        lo = r.choice([25, 30, 35, 40, 45, 50, 55])
        hi = lo + r.choice([4, 9])
        return dict(q=f"How many bank customers aged {lo} to {hi} are in the income category \"{income}\"?",
                    sql=f"SELECT COUNT(*) FROM bank WHERE income_category = {lit(income)} AND customer_age BETWEEN {lo} AND {hi}",
                    type="number", tables=["bank"], nonzero=True)

    def invoices_overdue(r):
        currency = r.choice(["USD", "EUR", "GBP"])
        year = r.choice(p.years)
        status = r.choice(["overdue", "open", "paid"])
        return dict(q=f"How many {currency} invoices issued in {year} have the status {status}?",
                    sql=f"SELECT COUNT(*) FROM invoices WHERE currency = {lit(currency)} AND status = {lit(status)} "
                        f"AND substr(issued_on, 1, 4) = {lit(str(year))}", type="number", tables=["invoices"], nonzero=True)

    def market_count(r):
        line = r.choice(p.market_lines)
        city = r.choice(p.market_cities)
        payment = r.choice(p.market_payments)
        return dict(q=f"How many supermarket purchases of {line} in {city} were paid by {payment}?",
                    sql=f'SELECT COUNT(*) FROM supermarket WHERE "Product line" = {lit(line)} AND City = {lit(city)} '
                        f"AND Payment = {lit(payment)}", type="number", tables=["supermarket"], nonzero=True)

    def products_discontinued(r):
        supplier = r.choice(p.suppliers)
        category = r.choice(p.categories)
        flag = r.choice([True, False])
        word = "discontinued" if flag else "still sold (not discontinued)"
        return dict(q=f"How many {category} products from {supplier} are {word}?",
                    sql=f"SELECT COUNT(*) FROM products WHERE supplier = {lit(supplier)} AND category = {lit(category)} "
                        f"AND discontinued = {lit('true' if flag else 'false')}", type="number", tables=["products"], nonzero=True)

    return [orders_status, customers_segment, tickets_priority, employees_active, forbes_count, nba_games, bank_income,
            invoices_overdue, market_count, products_discontinued]


def sum_templates(p):
    def revenue_month(r):
        ym = r.choice(p.months)
        channel = r.choice(["web", "direct", "partner"])
        return dict(q=f"What was the total value of completed {channel} orders placed in {month_name(ym)}?",
                    sql=f"SELECT ROUND(SUM(total), 2) FROM orders WHERE status = 'completed' AND channel = {lit(channel)} "
                        f"AND substr(order_date, 1, 7) = {lit(ym)}", type="number", tables=["orders"])

    def satisfaction(r):
        priority = r.choice(["low", "medium", "high", "urgent"])
        year = r.choice([2024, 2025])
        quarter = r.choice([1, 2, 3, 4])
        months = ", ".join(lit(f"{year}-{m:02d}") for m in range(3 * quarter - 2, 3 * quarter + 1))
        return dict(q=f"What was the average satisfaction score of closed {priority} priority tickets opened in Q{quarter} {year}?",
                    sql=f"SELECT ROUND(AVG(satisfaction), 2) FROM tickets WHERE status = 'closed' AND priority = {lit(priority)} "
                        f"AND satisfaction IS NOT NULL AND substr(opened_at, 1, 7) IN ({months})",
                    type="number", tables=["tickets"], restrictable=True)

    def salary(r):
        department = r.choice(p.departments)
        office = r.choice(p.offices)
        return dict(q=f"What is the average salary of {department} employees at the {office} office?",
                    sql=f"SELECT ROUND(AVG(salary), 2) FROM employees WHERE department = {lit(department)} AND office = {lit(office)}",
                    type="number", tables=["employees"])

    def nba_team_points(r):
        team = r.choice(p.nba_teams)
        year = r.choice(p.nba_years)
        return dict(q=f"How many points did {team} players score in total in the {year} NBA regular season?",
                    sql=f"SELECT SUM(PTS) FROM nba WHERE TEAM = {lit(team)} AND year = {lit(year)} AND Season_type = 'Regular Season'",
                    type="number", tables=["nba"])

    def forbes_age(r):
        country = r.choice(p.forbes_countries)
        made = r.choice([True, False])
        word = "self-made" if made else "not self-made"
        return dict(q=f"What is the average age of the {word} billionaires from {country} in the Forbes list?",
                    sql=f"SELECT ROUND(AVG(age), 2) FROM forbes WHERE country = {lit(country)} AND selfMade = {lit('true' if made else 'false')}",
                    type="number", tables=["forbes"])

    def market_income(r):
        city = r.choice(p.market_cities)
        line = r.choice(p.market_lines)
        month = r.choice([1, 2, 3])
        return dict(q=f"What was the total gross income from {line} sales at the {city} supermarket in {calendar.month_name[month]} 2019?",
                    sql=f'SELECT ROUND(SUM("gross income"), 2) FROM supermarket WHERE City = {lit(city)} AND "Product line" = {lit(line)} '
                        f"AND substr(Date, 1, 7) = '2019-{month:02d}'", type="number", tables=["supermarket"])

    def bank_credit(r):
        income = r.choice(p.bank_income)
        education = r.choice(p.bank_education)
        return dict(q=f"What is the average credit limit of bank customers with education level \"{education}\" in the income "
                      f"category \"{income}\"?",
                    sql=f"SELECT ROUND(AVG(credit_limit), 2) FROM bank WHERE income_category = {lit(income)} "
                        f"AND education_level = {lit(education)}",
                    type="number", tables=["bank"])

    def invoice_sum(r):
        currency = r.choice(["USD", "EUR", "GBP"])
        ym = r.choice(p.months)
        return dict(q=f"What is the total amount of {currency} invoices issued in {month_name(ym)}?",
                    sql=f"SELECT ROUND(SUM(amount), 2) FROM invoices WHERE currency = {lit(currency)} AND substr(issued_on, 1, 7) = {lit(ym)}",
                    type="number", tables=["invoices"])

    def warehouse_stock(r):
        warehouse = r.choice(p.warehouses)
        point = r.choice([10, 25, 50, 100])
        month = r.choice(["2025-06", "2025-07", "2025-08", "2025-09", "2025-10", "2025-11", "2025-12"])
        return dict(q=f"How many units are on hand in total across {warehouse} warehouse SKUs with a reorder point of {point} "
                      f"that were last counted in {month_name(month)}?",
                    sql=f"SELECT SUM(on_hand) FROM inventory WHERE warehouse = {lit(warehouse)} AND reorder_point = {point} "
                        f"AND substr(last_counted, 1, 7) = {lit(month)}", type="number", tables=["inventory"])

    def order_quantity(r):
        ym = r.choice(p.months)
        discount = r.choice([0, 5, 10, 15, 20])
        return dict(q=f"What was the average quantity per order for orders placed in {month_name(ym)} with a {discount}% discount?",
                    sql=f"SELECT ROUND(AVG(quantity), 2) FROM orders WHERE discount_pct = {discount} AND substr(order_date, 1, 7) = {lit(ym)}",
                    type="number", tables=["orders"])

    return [revenue_month, satisfaction, salary, nba_team_points, forbes_age, market_income, bank_credit, invoice_sum,
            warehouse_stock, order_quantity]


def topk_templates(p):
    def forbes_countries(r):
        category = r.choice(p.forbes_categories)
        k = r.choice([2, 3])
        return dict(q=f"Which {k} countries have the most billionaires in the Forbes list's {category} category?",
                    sql=f"SELECT country AS label, COUNT(*) AS metric FROM forbes WHERE category = {lit(category)} "
                        f"GROUP BY country ORDER BY metric DESC, label", topk=k, type="list[category]", tables=["forbes"])

    def nba_team(r):
        year = r.choice(p.nba_years)
        stat, word = r.choice([("PTS", "points"), ("REB", "rebounds"), ("AST", "assists"), ("BLK", "blocks"), ("STL", "steals")])
        return dict(q=f"Which team's players recorded the most total {word} in the {year} NBA regular season? Give the team abbreviation.",
                    sql=f"SELECT TEAM AS label, SUM({stat}) AS metric FROM nba WHERE year = {lit(year)} AND Season_type = 'Regular Season' "
                        f"GROUP BY TEAM ORDER BY metric DESC, label", topk=1, type="category", tables=["nba"])

    def channel_revenue(r):
        ym = r.choice(p.months)
        category = r.choice(["Hardware", "Software", "Services", "Accessories", "Training"])
        return dict(q=f"Which sales channel brought in the most completed-order value in {month_name(ym)}, counting only {category} products?",
                    sql=f"SELECT o.channel AS label, SUM(o.total) AS metric FROM orders o JOIN products pr ON pr.product_id = o.product_id "
                        f"WHERE o.status = 'completed' AND pr.category = {lit(category)} AND substr(o.order_date, 1, 7) = {lit(ym)} "
                        f"GROUP BY o.channel ORDER BY metric DESC, label", topk=1, type="category", tables=["orders", "products"])

    def ticket_categories(r):
        year = r.choice([2024, 2025])
        quarter = r.choice([1, 2, 3, 4])
        priority = r.choice(["medium", "high", "urgent"])
        k = r.choice([2, 3])
        months = ", ".join(lit(f"{year}-{m:02d}") for m in range(3 * quarter - 2, 3 * quarter + 1))
        return dict(q=f"Which {k} ticket categories had the most {priority} priority tickets opened in Q{quarter} {year}?",
                    sql=f"SELECT category AS label, COUNT(*) AS metric FROM tickets WHERE priority = {lit(priority)} "
                        f"AND substr(opened_at, 1, 7) IN ({months}) GROUP BY category ORDER BY metric DESC, label",
                    topk=k, type="list[category]", tables=["tickets"], restrictable=True)

    def office_salary(r):
        department = r.choice(p.departments)
        best = r.choice([True, False])
        word = "highest" if best else "lowest"
        return dict(q=f"Which office has the {word} average salary for {department} employees?",
                    sql=f"SELECT office AS label, AVG(salary) AS metric FROM employees WHERE department = {lit(department)} "
                        f"GROUP BY office ORDER BY metric {'DESC' if best else 'ASC'}, label", topk=1, type="category", tables=["employees"])

    def market_line(r):
        city = r.choice(p.market_cities)
        month = r.choice([1, 2, 3])
        gender = r.choice(["Female", "Male"])
        return dict(q=f"Which product line had the highest total sales to {gender.lower()} customers at the {city} supermarket in "
                      f"{calendar.month_name[month]} 2019?",
                    sql=f'SELECT "Product line" AS label, SUM(Total) AS metric FROM supermarket WHERE City = {lit(city)} '
                        f"AND Gender = {lit(gender)} AND substr(Date, 1, 7) = '2019-{month:02d}' GROUP BY label ORDER BY metric DESC, label",
                    topk=1, type="category", tables=["supermarket"])

    def bank_education(r):
        income = r.choice(p.bank_income)
        gender = r.choice(["F", "M"])
        word = "women" if gender == "F" else "men"
        return dict(q=f"Among bank customers who are {word} in the income category \"{income}\", which education level has the "
                      f"highest average utilization ratio?",
                    sql=f"SELECT education_level AS label, AVG(avg_utilization_ratio) AS metric FROM bank WHERE income_category = {lit(income)} "
                        f"AND gender = {lit(gender)} GROUP BY education_level ORDER BY metric DESC, label", topk=1, type="category",
                    tables=["bank"])

    def invoice_customers(r):
        currency = r.choice(["USD", "EUR", "GBP"])
        year = r.choice(p.years)
        k = r.choice([2, 3])
        return dict(q=f"Which {k} customer IDs were invoiced the largest total amount in {currency} in {year}?",
                    sql=f"SELECT customer_id AS label, SUM(amount) AS metric FROM invoices WHERE currency = {lit(currency)} "
                        f"AND substr(issued_on, 1, 4) = {lit(str(year))} GROUP BY customer_id ORDER BY metric DESC, label",
                    topk=k, type="list[number]", tables=["invoices"])

    def supplier_products(r):
        category = r.choice(p.categories)
        year = r.choice(range(2018, 2026))
        return dict(q=f"Which supplier provides the most {category} products launched in {year}?",
                    sql=f"SELECT supplier AS label, COUNT(*) AS metric FROM products WHERE category = {lit(category)} "
                        f"AND substr(launched, 1, 4) = {lit(str(year))} GROUP BY supplier ORDER BY metric DESC, label",
                    topk=1, type="category", tables=["products"])

    def top_customers(r):
        ym = r.choice(p.months)
        k = r.choice([2, 3])
        return dict(q=f"Which {k} customer IDs spent the most on completed orders in {month_name(ym)}?",
                    sql=f"SELECT customer_id AS label, SUM(total) AS metric FROM orders WHERE status = 'completed' "
                        f"AND substr(order_date, 1, 7) = {lit(ym)} GROUP BY customer_id ORDER BY metric DESC, label",
                    topk=k, type="list[number]", tables=["orders"])

    return [forbes_countries, nba_team, channel_revenue, ticket_categories, office_salary, market_line, bank_education,
            invoice_customers, supplier_products, top_customers]


def join_templates(p):
    def segment_revenue(r):
        segment = r.choice(["Enterprise", "Mid-Market", "SMB"])
        country = r.choice(p.countries)
        year = r.choice(p.years)
        return dict(q=f"What was the total value of completed orders from {segment} customers in {country} in {year}?",
                    sql=f"SELECT ROUND(SUM(o.total), 2) FROM orders o JOIN customers c ON c.customer_id = o.customer_id "
                        f"WHERE o.status = 'completed' AND c.segment = {lit(segment)} AND c.country = {lit(country)} "
                        f"AND substr(o.order_date, 1, 4) = {lit(str(year))}", type="number", tables=["orders", "customers"])

    def category_units(r):
        category = r.choice(p.categories)
        channel = r.choice(["web", "direct", "partner"])
        ym = r.choice(p.months)
        return dict(q=f"How many units of {category} products were ordered through the {channel} channel in {month_name(ym)}?",
                    sql=f"SELECT SUM(o.quantity) FROM orders o JOIN products pr ON pr.product_id = o.product_id "
                        f"WHERE pr.category = {lit(category)} AND o.channel = {lit(channel)} AND substr(o.order_date, 1, 7) = {lit(ym)}",
                    type="number", tables=["orders", "products"])

    def manager_customers(r):
        name = r.choice(p.managers)
        segment = r.choice(["Enterprise", "Mid-Market", "SMB"])
        return dict(q=f"How many {segment} customers does {name} manage as account manager?",
                    sql=f"SELECT COUNT(*) FROM customers c JOIN employees e ON e.employee_id = c.account_manager_id "
                        f"WHERE e.full_name = {lit(name)} AND c.segment = {lit(segment)}", type="number",
                    tables=["customers", "employees"], nonzero=True)

    def country_tickets(r):
        country = r.choice(p.countries)
        ym = r.choice(p.ticket_months)
        return dict(q=f"How many tickets were opened by customers based in {country} in {month_name(ym)}?",
                    sql=f"SELECT COUNT(*) FROM tickets t JOIN customers c ON c.customer_id = t.customer_id "
                        f"WHERE c.country = {lit(country)} AND substr(t.opened_at, 1, 7) = {lit(ym)}", type="number",
                    tables=["tickets", "customers"], restrictable=True, nonzero=True)

    def overdue_region(r):
        segment = r.choice(["Enterprise", "Mid-Market", "SMB"])
        region = r.choice(p.regions)
        year = r.choice(p.years)
        return dict(q=f"What is the total amount of overdue invoices issued in {year} to {segment} customers in the {region} region?",
                    sql=f"SELECT ROUND(SUM(i.amount), 2) FROM invoices i JOIN customers c ON c.customer_id = i.customer_id "
                        f"WHERE i.status = 'overdue' AND c.segment = {lit(segment)} AND c.region = {lit(region)} "
                        f"AND substr(i.issued_on, 1, 4) = {lit(str(year))}", type="number", tables=["invoices", "customers"])

    def product_stock(r):
        name = r.choice(p.stocked)
        return dict(q=f"How many units of {name} are on hand across all warehouses?",
                    sql=f"SELECT SUM(i.on_hand) FROM inventory i JOIN products pr ON pr.product_id = i.product_id "
                        f"WHERE pr.name = {lit(name)}", type="number", tables=["inventory", "products"])

    def best_product(r):
        category = r.choice(p.categories)
        ym = r.choice(p.months)
        return dict(q=f"Which {category} product had the highest completed-order value in {month_name(ym)}? Give its name.",
                    sql=f"SELECT pr.name AS label, SUM(o.total) AS metric FROM orders o JOIN products pr ON pr.product_id = o.product_id "
                        f"WHERE o.status = 'completed' AND pr.category = {lit(category)} AND substr(o.order_date, 1, 7) = {lit(ym)} "
                        f"GROUP BY pr.name ORDER BY metric DESC, label", topk=1, type="category", tables=["orders", "products"])

    def best_manager(r):
        segment = r.choice(["Enterprise", "Mid-Market", "SMB"])
        country = r.choice(p.countries)
        return dict(q=f"Which account manager manages the most {segment} customers in {country}? Give their full name.",
                    sql=f"SELECT e.full_name AS label, COUNT(*) AS metric FROM customers c JOIN employees e ON e.employee_id = c.account_manager_id "
                        f"WHERE c.segment = {lit(segment)} AND c.country = {lit(country)} GROUP BY e.full_name ORDER BY metric DESC, label",
                    topk=1, type="category", tables=["customers", "employees"])

    def customer_orders(r):
        c = r.choice(p.customers)
        year = r.choice(p.years)
        return dict(q=f"How many orders did {c[1]} place in {year}?",
                    sql=f"SELECT COUNT(*) FROM orders o JOIN customers c ON c.customer_id = o.customer_id "
                        f"WHERE c.name = {lit(c[1])} AND substr(o.order_date, 1, 4) = {lit(str(year))}", type="number",
                    tables=["orders", "customers"], nonzero=True)

    def supplier_revenue(r):
        supplier = r.choice(p.suppliers)
        ym = r.choice(p.months)
        return dict(q=f"What was the total value of completed orders for products supplied by {supplier} in {month_name(ym)}?",
                    sql=f"SELECT ROUND(SUM(o.total), 2) FROM orders o JOIN products pr ON pr.product_id = o.product_id "
                        f"WHERE o.status = 'completed' AND pr.supplier = {lit(supplier)} AND substr(o.order_date, 1, 7) = {lit(ym)}",
                    type="number", tables=["orders", "products"])

    return [segment_revenue, category_units, manager_customers, country_tickets, overdue_region, product_stock,
            best_product, best_manager, customer_orders, supplier_revenue]


def as_of_templates(p):
    def on_hand(r):
        sku = r.choice(p.both_skus)
        return dict(q=f"As Connapse saw it on {{as_of}}, how many units of {sku} were on hand?",
                    sql=f"SELECT on_hand FROM inventory WHERE sku = {lit(sku)}", type="number", tables=["inventory"])

    def counted(r):
        sku = r.choice(p.both_skus)
        return dict(q=f"As Connapse saw it on {{as_of}}, when was {sku} last counted?",
                    sql=f"SELECT last_counted FROM inventory WHERE sku = {lit(sku)}", type="category", tables=["inventory"])

    def listed(r):
        sku = r.choice(p.only_one_skus)
        return dict(q=f"As Connapse saw it on {{as_of}}, did the inventory list {sku}?",
                    sql=f"SELECT CASE WHEN COUNT(*) > 0 THEN 'true' ELSE 'false' END FROM inventory WHERE sku = {lit(sku)}",
                    type="boolean", tables=["inventory"])

    def below_reorder(r):
        warehouse = r.choice(p.warehouses)
        point = r.choice([10, 25, 50, 100])
        return dict(q=f"As Connapse saw it on {{as_of}}, how many {warehouse} SKUs with a reorder point of {point} were at or below it?",
                    sql=f"SELECT COUNT(*) FROM inventory WHERE warehouse = {lit(warehouse)} AND reorder_point = {point} AND on_hand <= reorder_point",
                    type="number", tables=["inventory"])

    def product_total(r):
        name = r.choice(p.stocked)
        return dict(q=f"As Connapse saw it on {{as_of}}, how many units of {name} were on hand across all warehouses?",
                    sql=f"SELECT SUM(i.on_hand) FROM inventory i JOIN products pr ON pr.product_id = i.product_id WHERE pr.name = {lit(name)}",
                    type="number", tables=["inventory", "products"])

    def warehouse_total(r):
        warehouse = r.choice(p.warehouses)
        category = r.choice(["Hardware", "Accessories"])
        return dict(q=f"As Connapse saw it on {{as_of}}, how many units of {category} products were on hand in the {warehouse} warehouse?",
                    sql=f"SELECT SUM(i.on_hand) FROM inventory i JOIN products pr ON pr.product_id = i.product_id "
                        f"WHERE pr.category = {lit(category)} AND i.warehouse = {lit(warehouse)}", type="number", tables=["inventory", "products"])

    def sku_count(r):
        warehouse = r.choice(p.warehouses)
        stockout = r.choice([True, False])
        word = "had none on hand" if stockout else "had at least one unit on hand"
        return dict(q=f"As Connapse saw it on {{as_of}}, how many SKUs in the {warehouse} warehouse {word}?",
                    sql=f"SELECT COUNT(*) FROM inventory WHERE warehouse = {lit(warehouse)} AND on_hand {'=' if stockout else '>'} 0",
                    type="number", tables=["inventory"])

    def unit_cost(r):
        sku = r.choice(p.both_skus)
        return dict(q=f"As Connapse saw it on {{as_of}}, what was the on-hand value (units on hand times unit cost) of {sku}?",
                    sql=f"SELECT ROUND(on_hand * unit_cost, 2) FROM inventory WHERE sku = {lit(sku)}", type="number", tables=["inventory"])

    return [on_hand, counted, listed, below_reorder, product_total, warehouse_total, sku_count, unit_cost]


# ------------------------------------------------------------------ running gold SQL

class Reject(Exception):
    pass


def to_answer(rows, kind):
    if kind.startswith("list["):
        return [row[0] for row in rows]
    if len(rows) != 1 or len(rows[0]) != 1:
        raise Reject(f"scalar query returned {len(rows)} rows")
    value = rows[0][0]
    if value is None:
        raise Reject("null answer")
    if kind == "boolean":
        return value == "true"
    if kind == "category":
        return str(value)
    return value


def run(spec, con):
    """Runs a spec's SQL and returns (gold_sql, answer); rejects a top-k cut on a tie."""
    k = spec.get("topk")
    if not k:
        return spec["sql"], to_answer(con.execute(spec["sql"]).fetchall(), spec["type"])
    rows = con.execute(f"SELECT label, metric FROM ({spec['sql']}) LIMIT {k + 1}").fetchall()
    if len(rows) < k or (len(rows) > k and rows[k - 1][1] == rows[k][1]):
        raise Reject("tie at the top-k cut")
    gold = f"SELECT label FROM ({spec['sql']}) LIMIT {k}"
    return gold, to_answer(con.execute(gold).fetchall(), spec["type"])


def indistinct(a, b, kind):
    """True when TableAnswerGrader would accept either answer for the other, so the pair can't tell them apart."""
    def close(x, y):
        return x == y if isinstance(y, int) else abs(x - y) <= 0.01 * abs(y)
    if kind == "number":
        return close(a, b) or close(b, a)
    if kind == "list[number]":
        return len(a) == len(b) and all(close(x, y) or close(y, x) for x, y in zip(sorted(a), sorted(b)))
    if kind == "list[category]":
        return sorted(map(str, a)) == sorted(map(str, b))
    return a == b


def realize(spec, dbs, user, as_of_load):
    db = "restricted" if user == "restricted" else ("load1" if as_of_load == 1 else "admin")
    gold, answer = run(spec, dbs[db])
    if spec.get("nonzero") and answer == 0:
        raise Reject("zero count")
    record = {"question": spec["q"], "user": user, "answer_type": spec["type"], "answer": answer, "gold_sql": gold,
              "tables": spec["tables"], "as_of_load": as_of_load}
    if user == "restricted":
        _, admin = run(spec, dbs["admin"])
        if indistinct(admin, answer, spec["type"]):
            raise Reject("restricted answer grades the same as the admin answer")
        record["admin_answer"] = admin
    if as_of_load is not None:
        _, other = run(spec, dbs["admin" if as_of_load == 1 else "load1"])
        if indistinct(other, answer, spec["type"]):
            raise Reject("answers on the two loads grade the same")
    return record


def family(name, templates, dbs, rng, seen):
    """PER_FAMILY questions, cycling the templates. Two in three instances of a tickets template are asked as the restricted
    user; seen-as-of instances ask about the first load twice for every once about the second."""
    out = []
    uses = {t.__name__: 0 for t in templates}
    i = 0
    misses = 0
    attempts = 0
    while len(out) < PER_FAMILY:
        attempts += 1
        if attempts > PER_FAMILY * 400:
            raise SystemExit(f"{name}: could not find {PER_FAMILY} valid questions")
        template = templates[i % len(templates)]
        spec = template(rng)
        user = "restricted" if spec.get("restrictable") and uses[template.__name__] % 3 != 0 else "admin"
        as_of_load = (1 if uses[template.__name__] % 3 != 2 else 2) if name == "seen_as_of" else None
        key = spec["q"]
        try:
            if key in seen:
                raise Reject("repeated question")
            record = realize(spec, dbs, user, as_of_load)
        except Reject:
            # Move on, so a template that is used up or often ties can't stall the family; a tickets template gets
            # up to 50 tries first, so the restricted-user questions keep their share.
            misses += 1
            if not spec.get("restrictable") or misses >= 50:
                i += 1
                misses = 0
            continue
        seen.add(key)
        uses[template.__name__] += 1
        record = {"family": name, "template": template.__name__, **record}
        out.append(record)
        i += 1
        misses = 0
    return out


# ------------------------------------------------------------------ mixed set: policy documents + tables

SLA_HOURS = {"urgent": 8, "high": 36, "medium": 120, "low": 240}
SAFETY = {"Reno": 1.5, "Toronto": 1.5, "Rotterdam": 2, "Singapore": 3}
POLICIES = {
    "discount-approval-policy.md": """# Discount approval policy

Effective 1 January 2023. Owner: Sales Operations.

Every order discount needs approval from a sales director when it is above **12 percent**. Orders placed through the
partner channel need director approval at any discount above **8 percent**, because partner margins are already
reduced. Cancelled orders are exempt: an approval that was never used is not counted.

Discounts at or below the threshold are approved by the account owner and need no further sign-off.
""",
    "support-sla.md": f"""# Support response targets (SLA)

Effective 1 January 2024. Owner: Customer Support.

Every ticket has a resolution target measured from the moment it is opened until it is closed:

- urgent: within **{SLA_HOURS['urgent']} hours**
- high: within **{SLA_HOURS['high']} hours**
- medium: within **5 days** ({SLA_HOURS['medium']} hours)
- low: within **10 days** ({SLA_HOURS['low']} hours)

A ticket breaches the SLA when it is closed later than its target. Tickets that are still open or pending are reviewed
separately and are not counted as breaches in this report. The targets apply to every queue, including security.
""",
    "strategic-accounts.md": """# Strategic accounts

Effective 1 July 2025. Owner: Revenue Leadership.

A customer is a **strategic account** when either of these holds:

1. its credit limit is at least **120,000**, or
2. it is in the Enterprise segment and signed up before **1 January 2019**.

Strategic accounts get a named executive sponsor and quarterly business reviews.
""",
    "late-payment-fees.md": """# Late payment fees

Effective 1 January 2023. Owner: Finance.

An invoice paid more than **10 days** after its due date is charged a late fee of **2.5 percent** of the invoice
amount. Invoices in **GBP** are exempt under the United Kingdom trading agreement. Unpaid invoices are handled by
collections and are not charged a fee until they are paid.
""",
    "safety-stock.md": """# Safety stock rule

Effective 1 June 2025. Owner: Operations.

Each warehouse keeps stock above a safety level set from the SKU's reorder point, because shipping lead times differ:

| Warehouse | Safety level |
|---|---|
| Reno | 1.5 times the reorder point |
| Toronto | 1.5 times the reorder point |
| Rotterdam | 2 times the reorder point |
| Singapore | 3 times the reorder point |

A SKU is **short** when its units on hand are below its warehouse's safety level.
""",
    "commission-plan.md": """# Commission plan

Effective 1 January 2023. Owner: Sales Operations.

Account managers and account executives earn commission on **completed** orders from the customers assigned to
them:

- **3 percent** of the order total for orders through the direct channel,
- **1.5 percent** for orders through the web channel,
- nothing for partner orders, which the partner's own team is paid for.

Commission is credited in the month the order was placed.
""",
}


def mixed_questions(dbs, p, rng):
    a = dbs["admin"]
    sla_case = " ".join(f"WHEN {lit(k)} THEN {v}" for k, v in SLA_HOURS.items())
    safety_case = " ".join(f"WHEN {lit(k)} THEN {v}" for k, v in SAFETY.items())

    def discount(r):
        ym = r.choice(p.months)
        return ("discount-approval-policy.md", f"Under the discount approval policy, how many orders placed in {month_name(ym)} needed a sales director's approval?",
                f"SELECT COUNT(*) FROM orders WHERE status <> 'cancelled' AND substr(order_date, 1, 7) = {lit(ym)} "
                f"AND ((channel = 'partner' AND discount_pct > 8) OR (channel <> 'partner' AND discount_pct > 12))", ["orders"])

    def sla(r):
        priority = r.choice(list(SLA_HOURS))
        ym = r.choice(p.ticket_months)
        return ("support-sla.md", f"Under the support SLA, how many {priority} priority tickets opened in {month_name(ym)} were closed later than their target?",
                f"SELECT COUNT(*) FROM tickets WHERE status = 'closed' AND priority = {lit(priority)} AND substr(opened_at, 1, 7) = {lit(ym)} "
                f"AND (julianday(closed_at) - julianday(opened_at)) * 24 > CASE priority {sla_case} END", ["tickets"])

    def strategic(r):
        country = r.choice(p.countries)
        return ("strategic-accounts.md", f"How many strategic accounts are there in {country}?",
                f"SELECT COUNT(*) FROM customers WHERE country = {lit(country)} AND (credit_limit >= 120000 "
                f"OR (segment = 'Enterprise' AND signup_date < '2019-01-01'))", ["customers"])

    def late_fees(r):
        ym = r.choice(p.months[:-3])
        return ("late-payment-fees.md", f"Under the late payment fee policy, what is the total of late fees on invoices issued in {month_name(ym)}?",
                f"SELECT ROUND(SUM(amount * 0.025), 2) FROM invoices WHERE paid_on IS NOT NULL AND currency <> 'GBP' "
                f"AND substr(issued_on, 1, 7) = {lit(ym)} AND julianday(paid_on) - julianday(due_on) > 10", ["invoices"])

    def safety(r):
        warehouse = r.choice(p.warehouses)
        category = r.choice(["Hardware", "Accessories"])
        return ("safety-stock.md", f"Under the safety stock rule, how many {category} SKUs in the {warehouse} warehouse are short?",
                f"SELECT COUNT(*) FROM inventory i JOIN products pr ON pr.product_id = i.product_id WHERE i.warehouse = {lit(warehouse)} "
                f"AND pr.category = {lit(category)} AND i.on_hand < i.reorder_point * CASE i.warehouse {safety_case} END",
                ["inventory", "products"])

    def commission(r):
        name = r.choice(p.managers)
        year = r.choice(p.years)
        return ("commission-plan.md", f"Under the commission plan, how much commission did {name} earn on orders placed in {year}?",
                f"SELECT ROUND(SUM(o.total * CASE o.channel WHEN 'direct' THEN 0.03 WHEN 'web' THEN 0.015 ELSE 0 END), 2) "
                f"FROM orders o JOIN customers c ON c.customer_id = o.customer_id JOIN employees e ON e.employee_id = c.account_manager_id "
                f"WHERE o.status = 'completed' AND e.full_name = {lit(name)} AND substr(o.order_date, 1, 4) = {lit(str(year))}",
                ["orders", "customers", "employees"])

    out = []
    seen = set()
    for template in (discount, sla, strategic, late_fees, safety, commission):
        n = 0
        attempts = 0
        while n < 5:
            attempts += 1
            if attempts > 500:
                raise SystemExit(f"mixed {template.__name__}: could not find 5 valid questions")
            doc, question, sql, tables = template(rng)
            if question in seen:
                continue
            try:
                answer = to_answer(a.execute(sql).fetchall(), "number")
            except Reject:
                continue
            if answer == 0:
                continue
            seen.add(question)
            n += 1
            out.append({"id": f"mixed-{len(out) + 1:03d}", "split": "mixed", "family": "mixed", "template": template.__name__,
                        "question": question, "user": "admin", "answer_type": "number", "answer": answer, "gold_sql": sql,
                        "tables": tables, "as_of_load": None, "documents": [f"docs/{doc}"]})
    return out


# ------------------------------------------------------------------ output

FAMILIES = [("lookup", lookup_templates), ("filter_count", count_templates), ("sum_avg", sum_templates),
            ("group_topk", topk_templates), ("join", join_templates), ("seen_as_of", as_of_templates)]
FIELDS = ["id", "split", "family", "template", "question", "user", "answer_type", "answer", "admin_answer", "gold_sql",
          "tables", "as_of_load", "documents"]


def jsonl(records):
    lines = []
    for r in records:
        ordered = {k: r[k] for k in FIELDS if k in r}
        lines.append(json.dumps(ordered, ensure_ascii=False, sort_keys=False))
    return ("\n".join(lines) + "\n").encode("utf-8")


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def gz(data):
    buffer = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", fileobj=buffer, mtime=0, compresslevel=9) as f:
        f.write(data)
    return buffer.getvalue()


def build():
    dbs = open_databases()
    pools = Pools(dbs)
    rng = random.Random(SEED)
    seen = set()
    splits = {"tune": [], "test": [], "reserve1": [], "reserve2": []}
    for name, make in FAMILIES:
        records = family(name, make(pools), dbs, rng, seen)
        for start in range(0, PER_FAMILY, len(BLOCK)):
            pattern = BLOCK[:]
            rng.shuffle(pattern)
            for record, split in zip(records[start:start + len(BLOCK)], pattern):
                splits[split].append(record)
    for split, records in splits.items():
        for n, record in enumerate(records, 1):
            record["id"] = f"{split}-{n:03d}"
            record["split"] = split
    texts = [r["question"] for rs in splits.values() for r in rs]
    mixed = mixed_questions(dbs, pools, rng)
    texts += [r["question"] for r in mixed]
    if len(set(texts)) != len(texts):
        raise SystemExit("repeated question text")

    write(os.path.join(REPO_DIR, "questions.jsonl"), jsonl(splits["tune"] + splits["test"]))
    write(os.path.join(REPO_DIR, "reserve-1.jsonl.gz"), gz(jsonl(splits["reserve1"])))
    write(os.path.join(REPO_DIR, "reserve-2.jsonl.gz"), gz(jsonl(splits["reserve2"])))
    write(os.path.join(REPO_DIR, "mixed.jsonl"), jsonl(mixed))
    for name, text in POLICIES.items():
        write(os.path.join(REPO_DIR, "docs", name), text.encode("utf-8"))
    return splits, mixed


if __name__ == "__main__":
    built, mixed_set = build()
    for split_name, items in built.items():
        by_family = {}
        for item in items:
            by_family[item["family"]] = by_family.get(item["family"], 0) + 1
        restricted = sum(1 for item in items if item["user"] == "restricted")
        print(f"{split_name:9} {len(items):4}  restricted {restricted:3}  {by_family}")
    print(f"mixed     {len(mixed_set):4}")
