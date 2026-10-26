# 6. Workers in a Function App, the API on App Service, both from the same composition

Status: accepted

## Decision

The consumer, the relay and the reconciler run in an Azure Function App on the Elastic Premium plan: the
Kafka trigger scales per partition with runtime scale monitoring, timers run on one instance, and retry
policies are supported for the Kafka trigger. The operations API runs on App Service behind API Management,
which validates Entra tokens and rate-limits per caller. Both register the same services from
`Bridge.Hosting`. The API can also run the workers itself (`Bridge:RunWorkers`), which is how the
containers and the demo run.

## Consequences

- One managed identity calls Dataverse from both; Key Vault references hold the Kafka secret.
- The Function App's behaviour under the Kafka trigger is tested without the Functions host
  (`TriggerDeliveryTests` play the host), and its build and function indexing are checked in CI.
