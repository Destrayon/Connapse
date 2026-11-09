"""Builds the tables-v1 linked set (CON-298, CON-E6 ticket 1) for ticket 15's passage links.

Ten documents of a fictional company, each with prose and one table: five Word files, and five PDFs made from Word
files by LibreOffice. Each table is written from seeded rows that are also saved as the table's CSV copy, so the
document and the copy hold the same values. The script reads every table back out of its document and checks it
against the copy, and checks the promotion gate from the research doc: one header row, the same column count on every
row, and column types that parse on at least 95% of cells.

Questions: per document, two table questions that need counting or summing and one prose question for the linked set
(20 + 10 = 30), and the same again for the sealed reserve (reserve-linked.jsonl.gz). Table answers come from gold SQL
(SQLite) over the CSV copies; prose answers are facts written in the document text.

Usage:
    python eval/tools/tables/build_linked.py           # writes everything except the PDFs, then checks the PDFs
    python eval/tools/tables/build_linked.py --pdf     # also re-renders the PDFs with LibreOffice

The Word files are byte-stable (fixed zip timestamps). LibreOffice stamps each PDF with a creation time and ID, so the
PDFs are committed as built and pinned by SHA-256 in eval/MANIFEST.json, and are re-rendered only with --pdf.
"""
import argparse
import datetime as dt
import gzip
import io
import json
import os
import random
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from xml.sax.saxutils import escape

sys.path.insert(0, os.path.dirname(__file__))
from build_tables import FIRST, LAST  # noqa: E402
from common import REPO_DIR, connect, load_table, read_csv, write_csv  # noqa: E402

LINKED_DIR = os.path.join(REPO_DIR, "linked")
SEED = 20261110
SOFFICE = os.environ.get("SOFFICE", r"C:\Program Files\LibreOffice\program\soffice.exe")


def person(rng):
    return f"{rng.choice(FIRST)} {rng.choice(LAST)}"


def d(text):
    return dt.date.fromisoformat(text)


# ------------------------------------------------------------------ the ten documents
# Each document: slug, format, title, paragraphs, columns (name, kind, display), rows, table questions (4: two for the
# linked set, two for the reserve) and prose questions (2: one each). kind is int, num or date; display formats a cell
# in the document (the CSV copy keeps the plain value).

def money(v):
    return f"${v:,.2f}"


def thousands(v):
    return f"{v:,}"


def documents(rng):
    docs = []

    # 1. Fleet maintenance (docx)
    depots = ["Dallas", "Fort Worth", "Plano"]
    models = ["Transit 350", "Sprinter 2500", "ProMaster 1500", "e-Transit"]
    rows = []
    for i in range(24):
        rows.append([f"VAN-{101 + i}", rng.choice(depots), rng.choice(models), rng.randint(18000, 140000),
                     (d("2025-03-01") + dt.timedelta(days=rng.randrange(200))).isoformat(), round(rng.uniform(120, 4800), 2)])
    manager = person(rng)
    docs.append(dict(
        slug="fleet-maintenance-2025", fmt="docx", title="Fleet Maintenance Report, 2025",
        paragraphs=[
            f"This report covers the 24 delivery vans in the North Texas fleet. Fleet manager {manager} signs off every "
            "repair above $2,500 before work starts.",
            "Vans are serviced every 15,000 km or every six months, whichever comes first. Electric vans skip oil "
            "service but get a battery health check at each visit, done by the Plano depot only.",
            "The table lists each van's depot, model, odometer reading and the cost of its last service.",
        ],
        columns=[("vehicle", "text", str), ("depot", "text", str), ("model", "text", str), ("mileage_km", "int", thousands),
                 ("last_service", "date", str), ("service_cost", "num", money)],
        rows=rows,
        table_q=[
            ("In the 2025 fleet maintenance report, how many vans based at the Dallas depot have more than 80,000 km on the odometer?",
             "SELECT COUNT(*) FROM t WHERE depot = 'Dallas' AND mileage_km > 80000", "number"),
            ("In the 2025 fleet maintenance report, what was the total cost of the last services of the Sprinter 2500 vans?",
             "SELECT ROUND(SUM(service_cost), 2) FROM t WHERE model = 'Sprinter 2500'", "number"),
            ("In the 2025 fleet maintenance report, how many vans had their last service in June 2025 or later?",
             "SELECT COUNT(*) FROM t WHERE last_service >= '2025-06-01'", "number"),
            ("In the 2025 fleet maintenance report, what is the average odometer reading of the Plano vans, in km?",
             "SELECT ROUND(AVG(mileage_km), 2) FROM t WHERE depot = 'Plano'", "number"),
        ],
        prose_q=[
            ("According to the 2025 fleet maintenance report, who must sign off repairs above $2,500?", manager, "category"),
            ("According to the 2025 fleet maintenance report, which depot does the battery health checks on electric vans?",
             "Plano", "category"),
        ]))

    # 2. Clinic staffing (pdf)
    clinics = ["Northside", "Lakeview", "Riverbend", "Oak Hill"]
    roles = ["Nurse", "Physician", "Pharmacist", "Receptionist", "Lab Technician"]
    rows = []
    for clinic in clinics:
        for role in rng.sample(roles, 5):
            rows.append([clinic, role, rng.randint(1, 14), rng.choice([20, 24, 32, 36, 40]),
                         round(rng.uniform(18, 140), 2)])
    director = person(rng)
    docs.append(dict(
        slug="clinic-staffing-plan", fmt="pdf", title="Clinic Staffing Plan, Winter 2025",
        paragraphs=[
            f"Medical director {director} approved this plan on 3 November 2025. It sets headcount for the winter "
            "season, when flu visits roughly double.",
            "Any clinic that drops below two nurses on a shift must call the float pool, which is run from Lakeview. "
            "Overtime is capped at eight hours per person per week.",
            "Planned headcount, weekly hours per person and hourly rates by clinic and role:",
        ],
        columns=[("clinic", "text", str), ("role", "text", str), ("headcount", "int", str), ("weekly_hours", "int", str),
                 ("hourly_rate", "num", money)],
        rows=rows,
        table_q=[
            ("In the winter 2025 clinic staffing plan, how many nurses are planned across all clinics?",
             "SELECT SUM(headcount) FROM t WHERE role = 'Nurse'", "number"),
            ("In the winter 2025 clinic staffing plan, what is the planned weekly pay for all Northside staff (headcount times weekly hours times hourly rate)?",
             "SELECT ROUND(SUM(headcount * weekly_hours * hourly_rate), 2) FROM t WHERE clinic = 'Northside'", "number"),
            ("In the winter 2025 clinic staffing plan, how many clinic and role pairs have an hourly rate above $60?",
             "SELECT COUNT(*) FROM t WHERE hourly_rate > 60", "number"),
            ("In the winter 2025 clinic staffing plan, how many staff in total are planned at Riverbend?",
             "SELECT SUM(headcount) FROM t WHERE clinic = 'Riverbend'", "number"),
        ],
        prose_q=[
            ("According to the winter 2025 clinic staffing plan, which clinic runs the float pool?", "Lakeview", "category"),
            ("According to the winter 2025 clinic staffing plan, what is the overtime cap per person per week, in hours?", 8, "number"),
        ]))

    # 3. Warehouse audit (pdf)
    rows = []
    for i in range(26):
        rows.append([f"B-{rng.choice('ABCDE')}{rng.randint(10, 99)}-{i + 1:02d}", rng.choice(["Reno", "Rotterdam", "Toronto"]),
                     rng.randint(5, 120), rng.choice([0, 0, 0, 1, 1, 2, 3, 5]),
                     (d("2025-09-01") + dt.timedelta(days=rng.randrange(45))).isoformat()])
    auditor = person(rng)
    docs.append(dict(
        slug="warehouse-audit-q3", fmt="pdf", title="Warehouse Bin Audit, Q3 2025",
        paragraphs=[
            f"Lead auditor {auditor} counted a sample of 26 bins across three warehouses between September and mid "
            "October 2025.",
            "A bin with three or more discrepancies is recounted within five working days. The audit found the Rotterdam "
            "scanner firmware to be out of date, and it was updated on 20 October.",
            "Bins audited, SKUs counted and discrepancies found:",
        ],
        columns=[("bin", "text", str), ("warehouse", "text", str), ("skus_counted", "int", str), ("discrepancies", "int", str),
                 ("audited_on", "date", str)],
        rows=rows,
        table_q=[
            ("In the Q3 2025 warehouse bin audit, how many bins need a recount under the audit's recount rule?",
             "SELECT COUNT(*) FROM t WHERE discrepancies >= 3", "number"),
            ("In the Q3 2025 warehouse bin audit, how many SKUs were counted in Reno bins in total?",
             "SELECT SUM(skus_counted) FROM t WHERE warehouse = 'Reno'", "number"),
            ("In the Q3 2025 warehouse bin audit, how many discrepancies were found in Toronto in total?",
             "SELECT SUM(discrepancies) FROM t WHERE warehouse = 'Toronto'", "number"),
            ("In the Q3 2025 warehouse bin audit, how many bins were audited in October 2025?",
             "SELECT COUNT(*) FROM t WHERE substr(audited_on, 1, 7) = '2025-10'", "number"),
        ],
        prose_q=[
            ("According to the Q3 2025 warehouse bin audit, within how many working days must a flagged bin be recounted?", 5, "number"),
            ("According to the Q3 2025 warehouse bin audit, which warehouse had out-of-date scanner firmware?", "Rotterdam", "category"),
        ]))

    # 4. Marketing campaigns (docx)
    rows = []
    channels = ["Search", "Social", "Email", "Events", "Display"]
    names = ["Spring Launch", "Partner Push", "Webinar Series", "Retargeting", "Trade Show", "Holiday Promo", "Brand Lift",
             "Free Trial", "Case Study", "Newsletter", "Referral", "Upgrade Drive", "Back to School", "Summer Sale",
             "Product Tour", "Analyst Day", "Customer Stories", "Year in Review"]
    for i, name in enumerate(names):
        spend = round(rng.uniform(2000, 60000), 2)
        leads = rng.randint(40, 2400)
        rows.append([name, rng.choice(channels), spend, leads, rng.randint(1, max(2, leads // 8)),
                     (d("2025-01-06") + dt.timedelta(days=14 * i + rng.randrange(10))).isoformat()])
    cmo = person(rng)
    docs.append(dict(
        slug="marketing-campaigns-2025", fmt="docx", title="Marketing Campaign Results, 2025",
        paragraphs=[
            f"This summary was prepared for chief marketing officer {cmo}. It covers the 18 campaigns that ran in 2025.",
            "A lead counts once it books a demo. A conversion is a signed order within 90 days of the lead. Next year's "
            "budget moves 20 percent of display spend to email.",
            "Results by campaign:",
        ],
        columns=[("campaign", "text", str), ("channel", "text", str), ("spend", "num", money), ("leads", "int", thousands),
                 ("conversions", "int", str), ("start_date", "date", str)],
        rows=rows,
        table_q=[
            ("In the 2025 marketing campaign results, how much was spent in total on Social campaigns?",
             "SELECT ROUND(SUM(spend), 2) FROM t WHERE channel = 'Social'", "number"),
            ("In the 2025 marketing campaign results, how many conversions did campaigns starting in the first half of 2025 bring in?",
             "SELECT SUM(conversions) FROM t WHERE start_date < '2025-07-01'", "number"),
            ("In the 2025 marketing campaign results, how many campaigns produced more than 1,000 leads?",
             "SELECT COUNT(*) FROM t WHERE leads > 1000", "number"),
            ("In the 2025 marketing campaign results, what was the total number of leads from Search and Email campaigns together?",
             "SELECT SUM(leads) FROM t WHERE channel IN ('Search', 'Email')", "number"),
        ],
        prose_q=[
            ("According to the 2025 marketing campaign results, within how many days of the lead must an order be signed to count as a conversion?", 90, "number"),
            ("According to the 2025 marketing campaign results, what share of display spend moves to email next year, in percent?", 20, "number"),
        ]))

    # 5. IT asset register (docx)
    rows = []
    types = ["Laptop", "Monitor", "Phone", "Docking station", "Desktop"]
    departments = ["Finance", "Engineering", "Sales", "Support"]
    price = {"Laptop": (900, 2600), "Monitor": (150, 700), "Phone": (300, 1200), "Docking station": (120, 350), "Desktop": (700, 2000)}
    for i in range(28):
        kind = rng.choice(types)
        rows.append([f"IT-{4100 + i}", kind, rng.choice(departments),
                     (d("2021-01-01") + dt.timedelta(days=rng.randrange(1700))).isoformat(),
                     round(rng.uniform(*price[kind]), 2), rng.choice(["In use", "In use", "In use", "Spare", "Retired"])])
    docs.append(dict(
        slug="it-asset-register", fmt="docx", title="IT Asset Register, November 2025",
        paragraphs=[
            "IT keeps this register of hardware for the Dallas office. Laptops are replaced after four years, "
            "monitors after six.",
            "Retired assets are wiped and sent to the recycler GreenCycle Partners every quarter. Spare assets are "
            "held in room 3B.",
            "Current register:",
        ],
        columns=[("asset_tag", "text", str), ("type", "text", str), ("department", "text", str), ("purchased", "date", str),
                 ("cost", "num", money), ("status", "text", str)],
        rows=rows,
        table_q=[
            ("In the November 2025 IT asset register, what is the total purchase cost of laptops that are in use?",
             "SELECT ROUND(SUM(cost), 2) FROM t WHERE type = 'Laptop' AND status = 'In use'", "number"),
            ("In the November 2025 IT asset register, how many assets bought before 2023 are still in use?",
             "SELECT COUNT(*) FROM t WHERE purchased < '2023-01-01' AND status = 'In use'", "number"),
            ("In the November 2025 IT asset register, how many assets does Engineering hold, in any status?",
             "SELECT COUNT(*) FROM t WHERE department = 'Engineering'", "number"),
            ("In the November 2025 IT asset register, what is the total purchase cost of all spare assets?",
             "SELECT ROUND(SUM(cost), 2) FROM t WHERE status = 'Spare'", "number"),
        ],
        prose_q=[
            ("According to the November 2025 IT asset register, which recycler takes retired assets?", "GreenCycle Partners", "category"),
            ("According to the November 2025 IT asset register, after how many years are monitors replaced?", 6, "number"),
        ]))

    # 6. Training completion (pdf)
    rows = []
    courses = ["Security Basics", "Data Privacy", "Safe Driving", "First Aid"]
    for _ in range(30):
        rows.append([person(rng), rng.choice(courses), rng.choice(["Finance", "Operations", "Sales", "Support"]),
                     (d("2025-04-01") + dt.timedelta(days=rng.randrange(210))).isoformat(), rng.randint(55, 100)])
    docs.append(dict(
        slug="training-completion-2025", fmt="pdf", title="Mandatory Training Completion, 2025",
        paragraphs=[
            "Every employee must complete the mandatory courses that apply to their role by 31 December 2025. The pass "
            "mark is 70 for every course except Data Privacy, where it is 80.",
            "Employees who fail retake the course within 30 days. Course content is provided by Learnwell Academy.",
            "Course completions recorded so far:",
        ],
        columns=[("employee", "text", str), ("course", "text", str), ("department", "text", str), ("completed_on", "date", str),
                 ("score", "int", str)],
        rows=rows,
        table_q=[
            ("In the 2025 mandatory training completion report, how many recorded completions are below the pass mark for their course?",
             "SELECT COUNT(*) FROM t WHERE score < CASE course WHEN 'Data Privacy' THEN 80 ELSE 70 END", "number"),
            ("In the 2025 mandatory training completion report, what is the average score on Security Basics?",
             "SELECT ROUND(AVG(score), 2) FROM t WHERE course = 'Security Basics'", "number"),
            ("In the 2025 mandatory training completion report, how many completions were recorded for the Operations department?",
             "SELECT COUNT(*) FROM t WHERE department = 'Operations'", "number"),
            ("In the 2025 mandatory training completion report, how many completions were recorded in August 2025 or later?",
             "SELECT COUNT(*) FROM t WHERE completed_on >= '2025-08-01'", "number"),
        ],
        prose_q=[
            ("According to the 2025 mandatory training completion report, what is the pass mark for Data Privacy?", 80, "number"),
            ("According to the 2025 mandatory training completion report, who provides the course content?", "Learnwell Academy", "category"),
        ]))

    # 7. Supplier scorecard (pdf)
    rows = []
    suppliers = ["Northwind Components", "Contoso Supply", "Fabrikam Parts", "Tailspin Goods", "Wingtip Distribution",
                 "Litware Media", "Adventure Works", "Proseware", "Lucerne Metals", "Margie Freight", "Alpine Plastics",
                 "Coho Textiles", "Fourth Coffee", "Humongous Packaging", "Trey Electronics", "VanArsdel Tools"]
    for name in suppliers:
        orders = rng.randint(20, 900)
        rows.append([name, rng.choice(["Americas", "Europe", "Asia"]), orders, round(rng.uniform(78, 99.5), 1),
                     rng.randint(0, max(1, orders // 25))])
    docs.append(dict(
        slug="supplier-scorecard-2025", fmt="pdf", title="Supplier Scorecard, 2025",
        paragraphs=[
            "Procurement scores every active supplier once a year. A supplier below 90 percent on-time delivery is put "
            "on a performance plan; two years in a row ends the contract.",
            "Defects are counted per delivered order. The scorecard committee meets in Chicago each January.",
            "2025 scores:",
        ],
        columns=[("supplier", "text", str), ("region", "text", str), ("orders", "int", str), ("on_time_pct", "num", str),
                 ("defects", "int", str)],
        rows=rows,
        table_q=[
            ("In the 2025 supplier scorecard, how many suppliers go on a performance plan?",
             "SELECT COUNT(*) FROM t WHERE on_time_pct < 90", "number"),
            ("In the 2025 supplier scorecard, how many orders did European suppliers deliver in total?",
             "SELECT SUM(orders) FROM t WHERE region = 'Europe'", "number"),
            ("In the 2025 supplier scorecard, how many defects were found across all Asian suppliers?",
             "SELECT SUM(defects) FROM t WHERE region = 'Asia'", "number"),
            ("In the 2025 supplier scorecard, how many suppliers delivered more than 400 orders?",
             "SELECT COUNT(*) FROM t WHERE orders > 400", "number"),
        ],
        prose_q=[
            ("According to the 2025 supplier scorecard, in which city does the scorecard committee meet?", "Chicago", "category"),
            ("According to the 2025 supplier scorecard, below what on-time delivery percentage is a supplier put on a performance plan?", 90, "number"),
        ]))

    # 8. Energy usage (docx)
    rows = []
    for site in ["Dallas HQ", "Plano Depot", "Reno Warehouse"]:
        for month in range(1, 10):
            kwh = rng.randint(18000, 95000)
            rows.append([site, f"2025-{month:02d}-01", kwh, round(kwh * rng.uniform(0.09, 0.16), 2), rng.randint(60, 420)])
    docs.append(dict(
        slug="energy-usage-2025", fmt="docx", title="Energy Usage by Site, January to September 2025",
        paragraphs=[
            "Facilities tracks electricity use at our three largest sites. The target for 2025 is a 6 percent cut "
            "against 2024.",
            "Solar panels were installed on the Reno Warehouse roof in May 2025. The utility contract renews on 1 March "
            "2026.",
            "Monthly use, cost and peak demand (each row is the month starting on the date shown):",
        ],
        columns=[("site", "text", str), ("month", "date", str), ("kwh", "int", thousands), ("cost", "num", money),
                 ("peak_kw", "int", str)],
        rows=rows,
        table_q=[
            ("In the January to September 2025 energy usage report, how many kWh did the Dallas HQ use in total?",
             "SELECT SUM(kwh) FROM t WHERE site = 'Dallas HQ'", "number"),
            ("In the January to September 2025 energy usage report, what was the total electricity cost across all sites in Q2 2025?",
             "SELECT ROUND(SUM(cost), 2) FROM t WHERE month BETWEEN '2025-04-01' AND '2025-06-01'", "number"),
            ("In the January to September 2025 energy usage report, in how many site-months was peak demand above 300 kW?",
             "SELECT COUNT(*) FROM t WHERE peak_kw > 300", "number"),
            ("In the January to September 2025 energy usage report, what was the Reno Warehouse's total electricity cost from June to September?",
             "SELECT ROUND(SUM(cost), 2) FROM t WHERE site = 'Reno Warehouse' AND month >= '2025-06-01'", "number"),
        ],
        prose_q=[
            ("According to the January to September 2025 energy usage report, what is the 2025 energy reduction target, in percent?", 6, "number"),
            ("According to the January to September 2025 energy usage report, which site got solar panels in May 2025?", "Reno Warehouse", "category"),
        ]))

    # 9. Customer survey (pdf)
    rows = []
    for area in ["Onboarding", "Billing", "Support", "Product quality", "Delivery", "Documentation"]:
        for region in ["North America", "EMEA", "APAC"]:
            rows.append([area, region, rng.randint(40, 600), round(rng.uniform(2.6, 4.8), 2), rng.randint(-20, 65)])
    docs.append(dict(
        slug="customer-survey-2025", fmt="pdf", title="Customer Survey Summary, 2025",
        paragraphs=[
            "The annual customer survey ran from 1 to 30 September 2025 and was run by the research firm Brightline "
            "Insights. Scores are on a 1 to 5 scale.",
            "Any area scoring below 3.5 in a region gets an improvement owner. The full survey goes to the board in "
            "December.",
            "Responses, average score and net promoter score (NPS) by area and region:",
        ],
        columns=[("area", "text", str), ("region", "text", str), ("responses", "int", str), ("avg_score", "num", str),
                 ("nps", "int", str)],
        rows=rows,
        table_q=[
            ("In the 2025 customer survey summary, how many area and region pairs need an improvement owner?",
             "SELECT COUNT(*) FROM t WHERE avg_score < 3.5", "number"),
            ("In the 2025 customer survey summary, how many responses came from EMEA in total?",
             "SELECT SUM(responses) FROM t WHERE region = 'EMEA'", "number"),
            ("In the 2025 customer survey summary, how many area and region pairs have a negative NPS?",
             "SELECT COUNT(*) FROM t WHERE nps < 0", "number"),
            ("In the 2025 customer survey summary, how many responses were there about Billing across all regions?",
             "SELECT SUM(responses) FROM t WHERE area = 'Billing'", "number"),
        ],
        prose_q=[
            ("According to the 2025 customer survey summary, which firm ran the survey?", "Brightline Insights", "category"),
            ("According to the 2025 customer survey summary, below what average score does an area get an improvement owner?", 3.5, "number"),
        ]))

    # 10. Project portfolio (docx)
    rows = []
    projects = ["Billing rewrite", "Warehouse scanners", "CRM migration", "Mobile app", "Data warehouse", "Office move",
                "Security audit", "Partner portal", "Payroll upgrade", "Chat support", "Website refresh", "Route planning",
                "Fleet telematics", "SSO rollout", "Inventory forecasting", "Help center", "Contract tool", "Expense app",
                "Backup overhaul", "Call recording"]
    for name in projects:
        budget = rng.randint(20, 900) * 1000
        rows.append([name, person(rng), budget, round(budget * rng.uniform(0.1, 1.3), 2),
                     rng.choice(["On track", "On track", "At risk", "Late", "Done"]),
                     (d("2025-10-01") + dt.timedelta(days=rng.randrange(300))).isoformat()])
    docs.append(dict(
        slug="project-portfolio-q4", fmt="docx", title="Project Portfolio Review, Q4 2025",
        paragraphs=[
            "The portfolio board reviews every project above $20,000 each quarter. A project that has spent more than "
            "its budget must present a recovery plan at the next board.",
            "The board is chaired by the chief operating officer and meets on the first Tuesday of each quarter. "
            "Projects marked Done are archived after one review.",
            "Projects under review:",
        ],
        columns=[("project", "text", str), ("owner", "text", str), ("budget", "int", thousands), ("spent", "num", money),
                 ("status", "text", str), ("due", "date", str)],
        rows=rows,
        table_q=[
            ("In the Q4 2025 project portfolio review, how many projects must present a recovery plan?",
             "SELECT COUNT(*) FROM t WHERE spent > budget", "number"),
            ("In the Q4 2025 project portfolio review, what is the total budget of projects marked At risk or Late?",
             "SELECT SUM(budget) FROM t WHERE status IN ('At risk', 'Late')", "number"),
            ("In the Q4 2025 project portfolio review, how many projects are due in 2026?",
             "SELECT COUNT(*) FROM t WHERE due >= '2026-01-01'", "number"),
            ("In the Q4 2025 project portfolio review, how much has been spent in total on projects marked On track?",
             "SELECT ROUND(SUM(spent), 2) FROM t WHERE status = 'On track'", "number"),
        ],
        prose_q=[
            ("According to the Q4 2025 project portfolio review, who chairs the portfolio board?", "chief operating officer", "category"),
            ("According to the Q4 2025 project portfolio review, after how many reviews are Done projects archived?", 1, "number"),
        ]))
    return docs


# ------------------------------------------------------------------ Word files

ZIP_TIME = (1980, 1, 1, 0, 0, 0)
CONTENT_TYPES = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
                 '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                 '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                 '<Default Extension="xml" ContentType="application/xml"/>'
                 '<Override PartName="/word/document.xml" '
                 'ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
                 '</Types>')
RELS = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" '
        'Target="word/document.xml"/></Relationships>')
W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"


def run_xml(text, bold=False, size=None):
    props = ("<w:b/>" if bold else "") + (f'<w:sz w:val="{size}"/>' if size else "")
    return f'<w:r>{"<w:rPr>" + props + "</w:rPr>" if props else ""}<w:t xml:space="preserve">{escape(text)}</w:t></w:r>'


def para_xml(text, bold=False, size=None):
    return f"<w:p>{run_xml(text, bold, size)}</w:p>"


def table_xml(header, rows):
    border = "".join(f'<w:{side} w:val="single" w:sz="4" w:space="0" w:color="000000"/>'
                     for side in ("top", "left", "bottom", "right", "insideH", "insideV"))
    out = [f'<w:tbl><w:tblPr><w:tblW w:w="0" w:type="auto"/><w:tblBorders>{border}</w:tblBorders></w:tblPr>']
    out.append("<w:tblGrid>" + "".join('<w:gridCol w:w="1600"/>' for _ in header) + "</w:tblGrid>")
    for i, row in enumerate([header] + rows):
        cells = "".join(f'<w:tc><w:tcPr><w:tcW w:w="1600" w:type="dxa"/></w:tcPr><w:p>{run_xml(c, bold=i == 0, size=18)}</w:p></w:tc>'
                        for c in row)
        repeat = "<w:trPr><w:tblHeader/></w:trPr>" if i == 0 else ""
        out.append(f"<w:tr>{repeat}{cells}</w:tr>")
    out.append("</w:tbl>")
    return "".join(out)


def displayed(doc):
    header = [c[0] for c in doc["columns"]]
    rows = [[fmt(v) for v, (_, _, fmt) in zip(row, doc["columns"])] for row in doc["rows"]]
    return header, rows


def docx_bytes(doc):
    header, rows = displayed(doc)
    body = [para_xml(doc["title"], bold=True, size=32)]
    body += [para_xml(p) for p in doc["paragraphs"]]
    body.append(table_xml(header, rows))
    body.append(para_xml("Figures as recorded on the date of this report."))
    document = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<w:document xmlns:w="{W}"><w:body>'
                + "".join(body) + '<w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1080" w:right="1080" '
                'w:bottom="1080" w:left="1080" w:header="720" w:footer="720" w:gutter="0"/></w:sectPr></w:body></w:document>')
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in (("[Content_Types].xml", CONTENT_TYPES), ("_rels/.rels", RELS), ("word/document.xml", document)):
            info = zipfile.ZipInfo(name, ZIP_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o600 << 16
            z.writestr(info, data.encode("utf-8"))
    return buffer.getvalue()


def docx_table(data):
    """The first table of a Word file, as rows of cell text."""
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        xml = z.read("word/document.xml").decode("utf-8")
    table = re.search(r"<w:tbl>.*?</w:tbl>", xml, re.S).group(0)
    rows = []
    for tr in re.findall(r"<w:tr>.*?</w:tr>", table, re.S):
        rows.append([unescape("".join(re.findall(r"<w:t(?: [^>]*)?>(.*?)</w:t>", tc, re.S)))
                     for tc in re.findall(r"<w:tc>.*?</w:tc>", tr, re.S)])
    return rows


def unescape(text):
    return text.replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&")


# ------------------------------------------------------------------ checks

NUMBER = re.compile(r"^-?\$?-?[\d,]*\.?\d+$")
DATE = re.compile(r"^\d{4}-\d{2}-\d{2}$")


def parses(text, kind):
    if kind == "text":
        return text.strip() != ""
    if kind == "date":
        if not DATE.match(text):
            return False
        try:
            d(text)
            return True
        except ValueError:
            return False
    if not NUMBER.match(text):
        return False
    return kind == "num" or "." not in text


def promotion_gate(doc, table_rows):
    """One header row, the same column count on every row, and each column's type parsing on >= 95% of cells."""
    header = [c[0] for c in doc["columns"]]
    if table_rows[0] != header:
        raise SystemExit(f"{doc['slug']}: header row {table_rows[0]} != {header}")
    body = table_rows[1:]
    if any(len(r) != len(header) for r in body):
        raise SystemExit(f"{doc['slug']}: column count differs between rows")
    for i, (name, kind, _) in enumerate(doc["columns"]):
        ok = sum(parses(r[i], kind) for r in body) / len(body)
        if ok < 0.95:
            raise SystemExit(f"{doc['slug']}: column {name} parses as {kind} on {ok:.0%} of cells")


def pdf_check(doc, path):
    """Every displayed cell of the table appears in the PDF's text, in row order."""
    from pypdf import PdfReader
    text = " ".join(page.extract_text() or "" for page in PdfReader(path).pages)
    flat = " ".join(text.split())
    _, rows = displayed(doc)
    position = 0
    for row in rows:
        for cell in row:
            found = flat.find(cell, position)
            if found < 0:
                raise SystemExit(f"{path}: cell {cell!r} not found in order")
            position = found + len(cell)


def render_pdf(docx_path, out_dir):
    with tempfile.TemporaryDirectory() as profile:
        subprocess.run([SOFFICE, f"-env:UserInstallation=file:///{profile.replace(os.sep, '/')}", "--headless",
                        "--convert-to", "pdf", "--outdir", out_dir, docx_path], check=True, capture_output=True, timeout=180)


# ------------------------------------------------------------------ questions

WORDS = {1: "one", 2: "two", 3: "three", 4: "four", 5: "five", 6: "six", 7: "seven", 8: "eight", 9: "nine", 10: "ten"}


def questions(docs):
    linked, reserve = [], []
    for doc in docs:
        con = connect()
        load_table(con, "t", [os.path.join(LINKED_DIR, "tables", f"{doc['slug']}.csv")])
        document = f"linked/docs/{doc['slug']}.{doc['fmt']}"
        table_copy = f"linked/tables/{doc['slug']}.csv"
        for n, (question, sql, kind) in enumerate(doc["table_q"]):
            rows = con.execute(sql).fetchall()
            if len(rows) != 1 or rows[0][0] in (None, 0):
                raise SystemExit(f"{doc['slug']}: {sql} gave {rows}")
            gold = sql.replace(" FROM t", f' FROM "{doc["slug"]}"')
            record = {"kind": "table", "question": question, "answer_type": kind, "answer": rows[0][0], "gold_sql": gold,
                      "document": document, "table_copy": table_copy}
            (linked if n < 2 else reserve).append(record)
        for n, (question, answer, kind) in enumerate(doc["prose_q"]):
            text = " ".join(doc["paragraphs"])
            if str(answer) not in text and not re.search(rf"\b{WORDS.get(answer, '#')}\b", text):
                raise SystemExit(f"{doc['slug']}: prose answer {answer!r} is not in the text")
            record = {"kind": "prose", "question": question, "answer_type": kind, "answer": answer, "gold_sql": None,
                      "document": document, "table_copy": None}
            (linked if n == 0 else reserve).append(record)
    for name, records in (("linked", linked), ("reserve", reserve)):
        for i, record in enumerate(records, 1):
            record["id"] = f"{name}-{i:03d}"
    return linked, reserve


def jsonl(records):
    keys = ["id", "kind", "question", "answer_type", "answer", "gold_sql", "document", "table_copy"]
    return "".join(json.dumps({k: r[k] for k in keys}, ensure_ascii=False) + "\n" for r in records).encode("utf-8")


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def build(render):
    docs = documents(random.Random(SEED))
    docs_dir = os.path.join(LINKED_DIR, "docs")
    with tempfile.TemporaryDirectory() as scratch:
        for doc in docs:
            header = [c[0] for c in doc["columns"]]
            write_csv(os.path.join(LINKED_DIR, "tables", f"{doc['slug']}.csv"), header, doc["rows"])
            data = docx_bytes(doc)
            table_rows = docx_table(data)
            if table_rows[1:] != displayed(doc)[1]:
                raise SystemExit(f"{doc['slug']}: Word table differs from the rows written")
            promotion_gate(doc, table_rows)
            if doc["fmt"] == "docx":
                write(os.path.join(docs_dir, f"{doc['slug']}.docx"), data)
            elif render:
                source = os.path.join(scratch, f"{doc['slug']}.docx")
                write(source, data)
                render_pdf(source, docs_dir)
            if doc["fmt"] == "pdf":
                pdf_check(doc, os.path.join(docs_dir, f"{doc['slug']}.pdf"))
            # The CSV copy must hold the same values the document shows.
            copy_header, copy_rows = read_csv(os.path.join(LINKED_DIR, "tables", f"{doc['slug']}.csv"))
            if copy_header != header or len(copy_rows) != len(doc["rows"]):
                raise SystemExit(f"{doc['slug']}: CSV copy differs from the document table")
    linked, reserve = questions(docs)
    write(os.path.join(LINKED_DIR, "questions.jsonl"), jsonl(linked))
    buffer = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", fileobj=buffer, mtime=0, compresslevel=9) as f:
        f.write(jsonl(reserve))
    write(os.path.join(LINKED_DIR, "reserve-linked.jsonl.gz"), buffer.getvalue())
    return docs, linked, reserve


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--pdf", action="store_true", help="re-render the PDFs with LibreOffice")
    args = parser.parse_args()
    if args.pdf and not shutil.which(SOFFICE) and not os.path.exists(SOFFICE):
        raise SystemExit(f"LibreOffice not found at {SOFFICE}; set SOFFICE")
    built, linked_set, reserve_set = build(args.pdf)
    print(f"{len(built)} documents, {len(linked_set)} linked questions, {len(reserve_set)} reserve questions")
