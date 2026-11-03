# Refunds: how they work

A refund is a new ledger row that points at the original charge; the charge row is never edited.
Partial refunds are allowed up to the charged amount. Refunds older than 180 days go through the card
processor's dispute flow instead. Support agents issue refunds from the admin console.
