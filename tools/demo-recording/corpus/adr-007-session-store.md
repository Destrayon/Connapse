# ADR-007: Move the checkout session store to Redis

**Status:** Accepted · **Date:** 2026-09-14

## Context

Checkout sessions live in the ledger database today. At the September sale peak, session writes were
40% of the ledger's write load, and the session table's vacuum held locks that delayed charge rows by
up to 2 seconds (see the 2026-09-02 incident review).

## Decision

Move checkout sessions to Redis 7 with a 30-minute TTL. The ledger keeps only charges, refunds and fees.

## Consequences

- The ledger's write load drops by about 40%, and session vacuum no longer touches charge rows.
- A Redis restart logs shoppers out of checkout; carts survive because they live in the storefront.
- Ops adds Redis to the on-call runbook.
