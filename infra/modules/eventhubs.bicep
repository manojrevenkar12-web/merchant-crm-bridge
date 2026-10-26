// Kafka on Azure Event Hubs (the Kafka endpoint is on Standard and above). The platform team may run its
// own Kafka instead; then only the bootstrap servers and the secret change.
@minLength(6)
@maxLength(40)
param name string
param location string
param tags object
param partitions int

var topics = ['platform.merchant.events', 'crm.merchant.events', 'platform.merchant.events.dlq']

resource ns 'Microsoft.EventHub/namespaces@2024-01-01' = {
  name: 'evh-${name}'
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard', capacity: 1 }
  properties: {
    kafkaEnabled: true
    minimumTlsVersion: '1.2'
    disableLocalAuth: false
  }
}

resource hubs 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = [for t in topics: {
  parent: ns
  name: t
  properties: {
    partitionCount: partitions
    messageRetentionInDays: 7
  }
}]

resource bridgeGroup 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2024-01-01' = {
  parent: hubs[0]
  name: 'merchant-crm-bridge'
}

resource bridge 'Microsoft.EventHub/namespaces/authorizationRules@2024-01-01' = {
  parent: ns
  name: 'merchant-crm-bridge'
  properties: { rights: ['Listen', 'Send'] }
}

output bootstrapServers string = '${ns.name}.servicebus.windows.net:9093'
output namespaceName string = ns.name
output ruleName string = bridge.name
