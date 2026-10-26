# Working in this repository

## Fast feedback

- `./scripts/check.sh`: builds everything that builds offline, runs the .NET tests (unit, component, API
  contract), checks the deployment settings and, when `web/node_modules` exists, the console's checks.
- `dotnet run --project src/Bridge.Drills -- --quick`: the sync drills (about a minute).
- `cd web && npm run check`; `npx playwright test` against `dotnet run --project src/Bridge.Api` (demo mode).
- The Function App, plug-in assembly, PCF solution and Bicep build in CI (`.github/workflows/ci.yml`).

## Invariants (each has a test or a drill check; keep it that way)

- A CRM change and its event commit in one transaction (outbox plug-in, custom API). Never publish from a
  plug-in or a flow directly.
- Platform events are applied per aspect by version; never write an aspect from an older version, never set
  the lifecycle stage directly (it is derived).
- Every applied event writes its ledger row in the same changeset as the account (`If-Match` on the account,
  `If-None-Match: *` on the ledger).
- Throttling and 5xx are waited out; only contract violations, unknown merchants (after 3 deliveries) and
  repeated failures (after 8) are dead-lettered. Offsets are committed after the CRM write.
- The reconciler heals only from a newer platform version; equal versions with different values are drift
  findings, never overwritten.
- `dataverse/model.json` is the schema. Change it, then run `dotnet run --project src/Bridge.Alm -- plan --out
  alm/metadata-plan.json` and commit both. Choice values in code are checked against it.
- `contracts/openapi.json` is the API's contract; the API tests fail when an endpoint or a response drifts
  from it. Change both together.
- Secrets never in source or settings files: Key Vault references in Azure, user secrets locally. Demo tokens
  (`Auth:Mode=Local`) are refused outside Development and demo mode.

## Where settings live

`src/Bridge.Hosting/BridgeSettings.cs` (all options, with defaults), `src/Bridge.Api/appsettings*.json`,
`src/Bridge.Functions/local.settings.example.json`, `infra/*.bicepparam`, `alm/settings/*.json`.

## Console

Feature folders under `web/src/features`; shared UI, theme tokens and API client under `web/src/shared`.
Every number and colour comes from `shared/theme/tokens.css` or `tokens.ts`. Every view handles loading
(skeleton), failure (what happened, and a retry when retrying can help) and empty.
