// Elastic Premium: the Kafka trigger scales with runtime-driven scale monitoring there.
@minLength(6)
@maxLength(40)
param name string
param location string
param tags object
param identityId string
param identityClientId string
param identityPrincipalId string
param workspaceId string
param settings object

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stfn${take(replace(name, '-', ''), 20)}'
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
  }
}

// The host's storage with the managed identity, no keys: blob (leases, timers), queue and table.
var storageRoles = [
  'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' // Storage Blob Data Owner
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88' // Storage Queue Data Contributor
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor
]
resource storageAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in storageRoles: {
  scope: storage
  name: guid(storage.id, identityPrincipalId, role)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
  }
}]

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${name}'
  location: location
  tags: tags
  sku: { name: 'EP1', tier: 'ElasticPremium' }
  kind: 'elastic'
  properties: { reserved: true, maximumElasticWorkerCount: 10 }
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identityId
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      functionsRuntimeScaleMonitoringEnabled: true
      appSettings: [for s in items(union(settings, {
        FUNCTIONS_EXTENSION_VERSION: '~4'
        FUNCTIONS_WORKER_RUNTIME: 'dotnet-isolated'
        AzureWebJobsStorage__accountName: storage.name
        AzureWebJobsStorage__credential: 'managedidentity'
        AzureWebJobsStorage__clientId: identityClientId
      })): { name: s.key, value: s.value }]
    }
  }
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: site
  name: 'to-log-analytics'
  properties: {
    workspaceId: workspaceId
    logs: [ { category: 'FunctionAppLogs', enabled: true } ]
    metrics: [ { category: 'AllMetrics', enabled: true } ]
  }
}

output name string = site.name
