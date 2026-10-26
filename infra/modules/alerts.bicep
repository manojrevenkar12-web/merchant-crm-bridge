// What pages on-call: events that cannot be applied, and a consumer that keeps retrying (throttling or
// an outage that does not end). Both come from the Function App's logs in Application Insights.
param name string
param location string
param tags object
param appInsightsId string
param actionGroupId string

resource deadLetters 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-${name}-dead-letters'
  location: location
  tags: tags
  properties: {
    displayName: 'Merchant bridge: platform events dead-lettered'
    description: 'Events the bridge could not apply. Open the operations console, Dead letters.'
    severity: 2
    enabled: true
    scopes: [appInsightsId]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      allOf: [
        {
          query: 'traces | where message startswith "dead-lettered" | summarize count()'
          timeAggregation: 'Total'
          metricMeasureColumn: 'count_'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: [actionGroupId] }
  }
}

resource stuck 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-${name}-consumer-retrying'
  location: location
  tags: tags
  properties: {
    displayName: 'Merchant bridge: consumer retrying for 15 minutes'
    description: 'Dataverse throttles or is unavailable and the platform-events partition is held. See the runbook in docs/RUNBOOK.md.'
    severity: 2
    enabled: true
    scopes: [appInsightsId]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      allOf: [
        {
          query: 'traces | where message startswith "delivering" | summarize retries = count() by bin(timestamp, 5m) | where retries > 20 | summarize count()'
          timeAggregation: 'Total'
          metricMeasureColumn: 'count_'
          operator: 'GreaterThanOrEqual'
          threshold: 3
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: { actionGroups: [actionGroupId] }
  }
}
