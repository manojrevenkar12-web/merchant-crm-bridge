param name string
param location string
param tags object
param readerPrincipalId string
@description('Event Hubs namespace and authorization rule whose connection string is the Kafka SASL password.')
param eventHubsNamespace string
param eventHubsRule string

resource rule 'Microsoft.EventHub/namespaces/authorizationRules@2024-01-01' existing = {
  name: '${eventHubsNamespace}/${eventHubsRule}'
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 30
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

resource kafkaSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'kafka-connection-string'
  properties: { value: rule.listKeys().primaryConnectionString, contentType: 'Event Hubs Kafka SASL password' }
}

// Key Vault Secrets User: the apps resolve Key Vault references with the managed identity.
resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, readerPrincipalId, 'secrets-user')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output kafkaSecretUri string = kafkaSecret.properties.secretUri
