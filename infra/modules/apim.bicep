// The API's front door: the OpenAPI contract imported as is, a version set by path segment, Entra
// tokens validated at the gateway (the API validates them again), and rate limits per caller.
param name string
param location string
param tags object
param backendUrl string
param tenantId string
param audience string
param appInsightsId string
param appInsightsKey string
@allowed(['Consumption', 'StandardV2'])
param sku string

resource service 'Microsoft.ApiManagement/service@2023-05-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: { name: sku, capacity: sku == 'Consumption' ? 0 : 1 }
  properties: {
    publisherEmail: 'integration-team@example.com'
    publisherName: 'Merchant integration'
  }
}

resource logger 'Microsoft.ApiManagement/service/loggers@2023-05-01-preview' = {
  parent: service
  name: 'appinsights'
  properties: {
    loggerType: 'applicationInsights'
    resourceId: appInsightsId
    credentials: { instrumentationKey: appInsightsKey }
  }
}

resource versions 'Microsoft.ApiManagement/service/apiVersionSets@2023-05-01-preview' = {
  parent: service
  name: 'merchant-bridge'
  properties: { displayName: 'Merchant bridge', versioningScheme: 'Segment' }
}

resource api 'Microsoft.ApiManagement/service/apis@2023-05-01-preview' = {
  parent: service
  name: 'merchant-bridge-v1'
  properties: {
    displayName: 'Merchant bridge'
    path: 'merchant-bridge'
    apiVersion: 'v1'
    apiVersionSetId: versions.id
    protocols: ['https']
    serviceUrl: backendUrl
    subscriptionRequired: true
    format: 'openapi+json'
    value: loadTextContent('../../contracts/openapi.json')
  }
}

resource policy 'Microsoft.ApiManagement/service/apis/policies@2023-05-01-preview' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: replace(replace(loadTextContent('apim-policy.xml'), '{tenant}', tenantId), '{audience}', audience)
  }
}

resource diagnostics 'Microsoft.ApiManagement/service/apis/diagnostics@2023-05-01-preview' = {
  parent: api
  name: 'applicationinsights'
  properties: {
    loggerId: logger.id
    alwaysLog: 'allErrors'
    sampling: { samplingType: 'fixed', percentage: 100 }
    httpCorrelationProtocol: 'W3C'
  }
}

output gatewayUrl string = '${service.properties.gatewayUrl}/merchant-bridge'
