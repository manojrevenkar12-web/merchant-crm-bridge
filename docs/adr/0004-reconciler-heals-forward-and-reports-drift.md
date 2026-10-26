# 4. The reconciler heals forward and reports hand edits instead of overwriting them

Status: accepted

## Context

Events can be lost (retention, a bug on the platform, a topic recreated). And people with enough privileges
can still change platform-owned values in the CRM, for example during an incident.

## Decision

Every 15 minutes (and on demand) the reconciler reads the platform's view of every synced merchant through
its GraphQL API, in batches of 50. Where the platform has a newer version of an aspect, it applies the
snapshot through the same applier, so the same rules hold. Where the versions are equal but the values
differ, it writes an `mcb_driftfinding` marked *needs review* and changes nothing.

## Consequences

- Lost events are healed within one cycle, and the healing is visible (ledger rows from snapshots).
- A hand edit stays until someone decides; it is not silently undone, and it is not silently kept either.
- The reconciler takes the same lease pattern as the relay, so two hosts never reconcile at once.
