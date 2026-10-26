# 2. Platform events are applied per aspect by version, so any order converges

Status: accepted

## Context

Kafka keeps order within a partition, but the platform's own producers retry, its services publish
different facts about one merchant independently, and replays and reconciliation bring old facts back.
Applying events "as they come" makes the CRM's state depend on delivery order.

## Decision

Each platform fact belongs to an aspect (KYC, status, risk, volume) that carries the platform's version.
The applier writes an aspect only when the event's version is newer than the stored one; otherwise it
records the event as stale. Facts that only accumulate (terminals by terminal id, the platform account set
once) are idempotent upserts. The lifecycle stage is derived from the aspects, not set by events.

## Consequences

- Any interleaving and any duplication of the same events gives the same CRM state. A property test runs
  500 random orders and duplications; every drill checks it before the reconciler runs.
- A lost event leaves an aspect behind until a later event or the reconciler brings it forward.
- The CRM never moves an aspect backwards, even when an old event is replayed by hand.
