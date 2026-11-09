"""Shared helpers for the tables-v1 build: byte-stable CSV writing and loading CSVs into SQLite for gold SQL."""
import csv
import hashlib
import io
import os
import re
import sqlite3

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
DATASET = "tables"
VERSION = "v1"
CACHE_DIR = os.path.join(ROOT, "eval", ".cache", "datasets", DATASET, VERSION)
REPO_DIR = os.path.join(ROOT, "eval", "datasets", DATASET)

INTEGER = re.compile(r"-?(0|[1-9]\d*)")
REAL = re.compile(r"-?\d+\.\d+")


def format_cell(value):
    """One cell as text: empty for null, true/false for booleans, shortest round-trip repr for floats."""
    if value is None:
        return ""
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float):
        return "" if value != value else repr(value)
    return str(value)


def write_csv(path, header, rows):
    """Writes a CSV with LF line endings and minimal quoting; returns its SHA-256."""
    buffer = io.StringIO(newline="")
    writer = csv.writer(buffer, lineterminator="\n", quoting=csv.QUOTE_MINIMAL)
    writer.writerow(header)
    for row in rows:
        writer.writerow([format_cell(v) for v in row])
    data = buffer.getvalue().encode("utf-8")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)
    return hashlib.sha256(data).hexdigest()


def sha256(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def read_csv(path):
    with open(path, encoding="utf-8", newline="") as f:
        rows = list(csv.reader(f))
    return rows[0], rows[1:]


def column_type(values):
    """INTEGER, REAL or TEXT, from every non-empty value of a column."""
    present = [v for v in values if v != ""]
    if present and all(INTEGER.fullmatch(v) for v in present):
        return "INTEGER"
    if present and all(INTEGER.fullmatch(v) or REAL.fullmatch(v) for v in present):
        return "REAL"
    return "TEXT"


def convert(value, kind):
    if value == "":
        return None
    if kind == "INTEGER":
        return int(value)
    if kind == "REAL":
        return float(value)
    return value


def load_table(con, name, paths):
    """Loads one or more CSVs with the same header into a SQLite table, typing each column from its values."""
    header = None
    rows = []
    for path in paths:
        h, r = read_csv(path)
        if header is not None and h != header:
            raise ValueError(f"{path}: header differs from {paths[0]}")
        header = h
        rows.extend(r)
    kinds = [column_type([row[i] for row in rows]) for i in range(len(header))]
    columns = ", ".join(f'"{c}" {k}' for c, k in zip(header, kinds))
    con.execute(f'DROP TABLE IF EXISTS "{name}"')
    con.execute(f'CREATE TABLE "{name}" ({columns})')
    marks = ", ".join("?" for _ in header)
    con.executemany(f'INSERT INTO "{name}" VALUES ({marks})',
                    ([convert(v, k) for v, k in zip(row, kinds)] for row in rows))
    return dict(zip(header, kinds))


def connect():
    con = sqlite3.connect(":memory:")
    con.execute("PRAGMA case_sensitive_like = ON")
    return con
