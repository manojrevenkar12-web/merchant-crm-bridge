param name string
param location string
param tags object
param alertEmail string

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: false
  }
}

resource onCall 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-${name}'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'mcb-oncall'
    enabled: true
    emailReceivers: [
      { name: 'on-call', emailAddress: alertEmail, useCommonAlertSchema: true }
    ]
  }
}

output workspaceId string = workspace.id
output appInsightsId string = insights.id
output appInsightsConnectionString string = insights.properties.ConnectionString
output appInsightsInstrumentationKey string = insights.properties.InstrumentationKey
output actionGroupId string = onCall.id
