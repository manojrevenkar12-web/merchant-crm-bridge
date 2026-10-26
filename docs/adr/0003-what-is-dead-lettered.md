# 3. Only what cannot be applied is dead-lettered; throttling is waited out

Status: accepted

## Context

Dataverse's service protection answers 429 with Retry-After (6,000 requests, 20 minutes of execution time
and 52 concurrent requests per user in a 5-minute window, by default). A consumer that dead-letters on
errors would, under throttling, move a merchant's later events past its earlier ones.

## Decision

- Contract violations (not a CloudEvent, unknown type or version, schema failure, key differs from subject)
  go to `platform.merchant.events.dlq` at once, with the reason, detail, source offset and deliveries in
  headers.
- An unknown merchant is delivered again up to three times (the CRM may still be committing it), then
  dead-lettered.
- 429 and 5xx rewind the partition and wait (Retry-After, else exponential backoff); they are never
  dead-lettered. In the Function App the same rule is an unbounded exponential retry policy on the Kafka
  trigger, waiting out short Retry-After values inside the invocation.
- Anything else is retried, then dead-lettered after eight deliveries so one message cannot stop a
  partition for good.
- Offsets are committed after the CRM write, not before.

## Consequences

- A long Dataverse outage holds the partition; an alert fires when the consumer keeps retrying.
- Operations replay a dead letter at most once from the console (`Sync.Operate`), after the cause is fixed.
