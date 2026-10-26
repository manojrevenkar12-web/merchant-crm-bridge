# 1. The CRM's events come from an outbox written inside the plug-in transaction

Status: accepted

## Context

When sales change a merchant's IBAN or pricing plan, the platform must hear about it exactly once and in
the order the changes happened. The obvious designs each lose something:

- A cloud flow or an asynchronous plug-in calling Kafka (or an HTTP endpoint) after the save can fail after
  the row committed: the change is in the CRM and never reaches the platform.
- A synchronous plug-in calling out *before* the commit can publish a change that then rolls back.
- Polling Dataverse for changed rows (change tracking, `versionnumber` watermarks, the Confluent source
  connector) gives at-least-once rows but no intent: it cannot say "onboarding was requested", and deletes
  and ordering across columns are best effort.

## Decision

A synchronous PreOperation plug-in on Update of account, filtered to the synced columns, writes an
`mcb_outboxmessage` row in the same database transaction as the change and raises `mcb_crmversion`, which
becomes the event's sequence. The custom API `mcb_RequestOnboarding` does the same for the hand-over. A relay
(a timer in the Function App) publishes pending rows to `crm.merchant.events` in commit order per merchant,
under a lease, with an idempotent producer, and marks them sent.

## Consequences

- A change and its event commit together or not at all.
- The relay can publish a row twice (crash after publish, before marking sent); the event id is stable, so
  the platform dedupes on it. This is the documented contract.
- The version is taken from the stored row (the pre-image), never from the request.
- Changes the bridge itself writes (platform-owned columns) never produce outbox rows: the plug-in skips the
  integration user, found through the environment variable `mcb_IntegrationUserId`.
