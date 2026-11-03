# Runbook: ledger lock waits above 500 ms

**Alert:** `ledger_lock_wait_ms > 500` for 5 minutes. Added after the 2026-09-02 incident.

1. Check whether autovacuum is running on `checkout_sessions`. If it is, let it finish; do not cancel it during a sale.
2. Raise the charge worker's queue concurrency from 8 to 12 with `halcyon-ops scale charge-worker 12`.
3. If waits stay above 500 ms for 15 more minutes, page the payments on-call lead.
