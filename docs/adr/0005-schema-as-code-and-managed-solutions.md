# 5. Dataverse schema as code, exported as a managed solution

Status: accepted

## Context

The bridge depends on precise metadata: alternate keys the consumer upserts by, column security on
platform-owned columns, choice values the code maps, plug-in steps with filtering attributes and images.
Building that by hand in the maker portal makes it drift from the code, and reviewers cannot see a change.

## Decision

`dataverse/model.json` is the single description. The emulator, the plug-in rules, the consistency tests and
`src/Bridge.Alm` all read it. `mcb-alm plan` turns it into the Web API requests that bring an environment to
it (committed as `alm/metadata-plan.json`, so a schema change shows in review as a change of requests);
`mcb-alm apply` runs them against the development environment inside the solution, looking before it
writes, so it can run on every merge. The pipeline then exports the solution managed and unmanaged with the
Power Platform CLI and imports the managed one into test and production with a deployment settings file per
environment (`alm/settings`), whose completeness is checked in CI.

## Consequences

- Downstream environments only ever receive managed solutions.
- Environment variable values and connections are per environment, filled from the GitHub environment.
- The application user and its role assignment are part of the pipeline, not a manual step.
