# Runbook

## Alert: platform events dead-lettered

1. Open the operations console, **Dead letters**. Each entry says why: *breaks the event contract*,
   *merchant not in the CRM* or *failed repeatedly*, with the detail and the source offset.
2. Contract: ask the platform team which producer sent it. If the bridge needs a new schema version, add it
   to `contracts/schemas` (and an upcaster when the change is not additive), release, then **Replay**.
3. Merchant not in the CRM: find out why the merchant is missing (created outside onboarding, deleted by
   hand). Once it exists, **Replay**.
4. Failed repeatedly: the detail has the exception. Fix the cause, then **Replay**.

A dead letter can be replayed once from the console; the replay carries `replay-of` and `replay-by` headers
and the original trace context.

## Alert: consumer retrying for 15 minutes

The partition is held because Dataverse throttles or answers 5xx. Nothing is lost; events wait.

1. Application Insights, Function App logs: `delivering ... again` lines show the reason (429 or 5xx).
2. 429: check the bridge user's request volume and whether a data migration or another integration uses
   the same application user. Service protection is per user; the bridge should have its own.
3. 5xx: check the Power Platform admin center service health for the region.
4. When it clears, the consumer catches up on its own. Check **Overview**: *Waiting* falls back to zero.

## Drift findings

A platform-owned value differs from the platform's at the same version: someone changed it in the CRM. Find
out who (audit history on the account), restore the platform's value or ask the platform team to publish the
corrected fact, then mark the finding resolved.

## The relay is not publishing

**Overview** shows the relay's last run. If it is old, check the Function App's `outbox-relay` executions. The
relay holds back the rest of a merchant's rows after a failure and marks a row stuck after five attempts;
stuck rows show their last error on the merchant's timeline.
