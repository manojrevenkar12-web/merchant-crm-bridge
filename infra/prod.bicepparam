using 'main.bicep'

param environment = 'prod'
param dataverseUrl = 'https://contoso-merchants-prod.crm4.dynamics.com/'
param platformGraphQlEndpoint = 'https://platform-prod.example.com/graphql'
param alertEmail = 'merchant-integration-oncall@example.com'
param corsOrigins = ['https://merchant-bridge-prod.example.com']
