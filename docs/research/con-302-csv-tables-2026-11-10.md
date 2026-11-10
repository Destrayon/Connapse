# How should an uploaded CSV become a typed table: dialect, types, header, rejects and loading?
**Date:** 2026-11-10
**Status:** Reviewed
**Built on:** tables-container-2026-11-02.md, con-301-table-storage-2026-11-09.md, docs/plans/con-e6-tables-container.md (Connapse MCP was not connected, so no corpus search)

## Executive summary
CON-302 copies DuckDB's CSV sniffer rather than inventing one. It picks the delimiter whose rows have the most consistent column count. It tries types in DuckDB's priority order (boolean, date, timestamp, timestamp with zone, whole number, number, text) over a sample. It decides whether the first row is a header by checking whether that row fails the types the rest of the column takes. Cells that don't fit their column's type are listed with row, column, value and reason, and their rows are left out, the way DuckDB's reject table works. Nothing is silently dropped. The column types and formats are saved in Frictionless Table Schema terms, and rows go into PostgreSQL by Npgsql binary COPY. A 100,000-row, five-column file loads in about 1.3 s locally.

## Sources
- **DuckDB CSV auto-detection** (primary): https://duckdb.org/docs/data/csv/auto_detection. Covers the dialect search over `, | ; \t`, the type priority, the 20,480-row sample, the header rule, the generated `column0…` names, and leading-zero numbers staying text.
- **DuckDB faulty CSV files** (primary): https://duckdb.org/docs/data/csv/reading_faulty_csv_files. The `reject_errors` table (line, column, value, error type) is the model for `TableReject` and the catalog's rejects list.
- **Frictionless Table Schema v2** (primary): https://datapackage.org/standard/table-schema/. Defines the field `type` and `format` and `decimalChar`, `groupChar`, `trueValues`, `falseValues` and `missingValues`. Connapse saves each column's guessed format in these terms so the guess can be read and corrected.
- **Sylvan.Data.Csv** (primary, source and README): https://github.com/MarkPflug/Sylvan. It is a streaming reader that raises `CsvFormatException` on malformed quoting. The sniffer treats that exception as ruling out a candidate delimiter.
- **Npgsql COPY** (primary): https://www.npgsql.org/doc/copy.html. Binary import is used through `ITableStore`, which CON-301 built.
- **CsCheck** (primary): https://github.com/AnthonyLloyd/CsCheck. Generates random inputs for the sniffer and loader properties.

## Decisions and why
- **The sample is spread across the file, not just its head.** A column that is numeric for 20,000 rows and then holds `n/a` should still be typed as numeric, with that one cell listed as a reject. If more than 1,000 cells fail, the reader takes this as a wrong guess and re-guesses from every row, which usually makes the column text.
- **Leading-zero numbers stay text** (`007`, ZIP codes), as in DuckDB, because a whole-number type would lose the zeros.
- **Date formats are tried in DuckDB's order**: ISO, then year-first, day-first, month-first. When both day-first and month-first fit a column, day-first wins.
- **Every CSV is its own table in whatever container it lands in.** Deleting the file retires its table through a trigger on `documents`, so a delete from any code path cleans it up. The one rule for which uploads become tables is `TableUploads.LoadsAsTable` (`.csv` only for now: `.tsv` isn't an accepted upload type yet).

## Known limits
- A CSV in a documents container no longer gets text chunks, so text search can't find it. The tables RAG eval baseline, which searched CSV text, measures something else from now on.
- The table load and the document's completion run in separate transactions. A superseded ingestion job can briefly reload over a newer one, and the newer job then reloads again.
