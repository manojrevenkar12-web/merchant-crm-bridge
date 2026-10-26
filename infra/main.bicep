// merchant-crm-bridge on Azure: the API on App Service behind API Management, the consumer, relay and
// reconciler in a Function App, Kafka on Event Hubs, secrets in Key Vault, one user-assigned managed
// identity for everything that calls Dataverse, telemetry in Application Insights.
//   az deployment group create -g rg-merchant-bridge-test -f infra/main.bicep -p infra/test.bicepparam
targetScope = 'resourceGroup'

@description('Short environment name: test, prod.')
@allowed(['test', 'prod'])
param environment string

param location string = resourceGroup().location

@description('The Dataverse environment the bridge syncs, e.g. https://contoso-merchants-test.crm4.dynamics.com/')
param dataverseUrl string

@description('Entra tenant that issues tokens for the API.')
param tenantId string = subscription().tenantId

@description('Application ID URI of the API app registration (its app roles: Sync.Read, Sync.Operate).')
param apiAudience string = 'api://merchant-crm-bridge'

@description('The payments platform GraphQL endpoint the reconciler reads.')
param platformGraphQlEndpoint string

@description('Where alerts go (on-call mailbox or a Teams channel e-mail).')
param alertEmail string

@description('Origins allowed to call the API from a browser (the operations console).')
param corsOrigins array = []

var name = 'mcb-${environment}'
var suffix = uniqueString(resourceGroup().id)
var tags = { application: 'merchant-crm-bridge', environment: environment }

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}'
  location: location
  tags: tags
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: { name: name, location: location, tags: tags, alertEmail: alertEmail }
}

module kafka 'modules/eventhubs.bicep' = {
  name: 'eventhubs'
  params: { name: '${name}-${suffix}', location: location, tags: tags, partitions: environment == 'prod' ? 12 : 4 }
}

module vault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: 'kv-${take(name, 10)}-${take(suffix, 8)}'
    location: location
    tags: tags
    readerPrincipalId: identity.properties.principalId
    eventHubsNamespace: kafka.outputs.namespaceName
    eventHubsRule: kafka.outputs.ruleName
  }
}

var bridgeSettings = {
  Bridge__Dataverse__EnvironmentUrl: dataverseUrl
  Bridge__Dataverse__Credential: 'ManagedIdentity'
  Bridge__Dataverse__ClientId: identity.properties.clientId
  Bridge__Kafka__BootstrapServers: kafka.outputs.bootstrapServers
  Bridge__Kafka__SecurityProtocol: 'SaslSsl'
  Bridge__Kafka__SaslMechanism: 'Plain'
  Bridge__Kafka__SaslUsername: '$ConnectionString'
  Bridge__Kafka__SaslPassword: '@Microsoft.KeyVault(SecretUri=${vault.outputs.kafkaSecretUri})'
  Bridge__Kafka__GroupId: 'merchant-crm-bridge'
  Bridge__Platform__GraphQlEndpoint: platformGraphQlEndpoint
  AZURE_CLIENT_ID: identity.properties.clientId
  APPLICATIONINSIGHTS_CONNECTION_STRING: monitoring.outputs.appInsightsConnectionString
}

module functions 'modules/functions.bicep' = {
  name: 'functions'
  params: {
    name: 'func-${name}-${take(suffix, 6)}'
    location: location
    tags: tags
    identityId: identity.id
    identityClientId: identity.properties.clientId
    identityPrincipalId: identity.properties.principalId
    workspaceId: monitoring.outputs.workspaceId
    settings: union(bridgeSettings, {
      KafkaBrokerList: kafka.outputs.bootstrapServers
      KafkaUsername: '$ConnectionString'
      KafkaPassword: '@Microsoft.KeyVault(SecretUri=${vault.outputs.kafkaSecretUri})'
      Bridge__RelaySchedule: '*/5 * * * * *'
      Bridge__ReconcileSchedule: '0 */15 * * * *'
    })
  }
}

module app 'modules/appservice.bicep' = {
  name: 'appservice'
  params: {
    name: 'app-${name}-${take(suffix, 6)}'
    location: location
    tags: tags
    identityId: identity.id
    workspaceId: monitoring.outputs.workspaceId
    settings: union(bridgeSettings, {
      Bridge__RunWorkers: 'false' // the Function App runs the consumer, relay and reconciler
      Bridge__Auth__Mode: 'Entra'
      Bridge__Auth__TenantId: tenantId
      Bridge__Auth__Audience: apiAudience
    })
    corsOrigins: corsOrigins
  }
}

module apim 'modules/apim.bicep' = {
  name: 'apim'
  params: {
    name: 'apim-${name}-${take(suffix, 6)}'
    location: location
    tags: tags
    backendUrl: 'https://${app.outputs.hostName}'
    tenantId: tenantId
    audience: apiAudience
    appInsightsId: monitoring.outputs.appInsightsId
    appInsightsKey: monitoring.outputs.appInsightsInstrumentationKey
    sku: environment == 'prod' ? 'StandardV2' : 'Consumption'
  }
}

module alerts 'modules/alerts.bicep' = {
  name: 'alerts'
  params: {
    name: name
    location: location
    tags: tags
    appInsightsId: monitoring.outputs.appInsightsId
    actionGroupId: monitoring.outputs.actionGroupId
  }
}

output apiGateway string = apim.outputs.gatewayUrl
output functionApp string = functions.outputs.name
output appService string = app.outputs.hostName
@description('Create a Dataverse application user for this client id and give it the Merchant Bridge Integration role and column security profile.')
output dataverseApplicationClientId string = identity.properties.clientId
