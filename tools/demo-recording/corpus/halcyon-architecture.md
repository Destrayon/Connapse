# Halcyon payments service: architecture

Halcyon takes card payments for the storefront and writes every charge to the ledger.

## Components

- **Gateway API** (ASP.NET Core) receives checkout requests and validates the cart total.
- **Charge worker** calls the card processor and retries failed calls with exponential backoff:
  200 ms, 400 ms, 800 ms, then gives up and marks the charge `needs_review`.
- **Ledger** is PostgreSQL 15 with one table per account book. Every charge, refund and fee is an
  append-only ledger row; nothing is updated in place.
- **Session store** keeps checkout sessions for 30 minutes.

## Idempotency

Every checkout request carries an `Idempotency-Key` header. The gateway stores the key with the
charge for 24 hours, so a client that retries after a timeout gets the first result back instead of
a second charge.

## Database failover

The ledger runs as a primary with one synchronous replica. On primary loss, the replica is promoted
by Patroni within 30 seconds. The charge worker pauses while the ledger is read-only and resumes on
its own; no charge is lost, because the worker only acknowledges a queue message after the ledger
row commits.
