using 'main.bicep'

param environment = 'test'
param dataverseUrl = 'https://contoso-merchants-test.crm4.dynamics.com/'
param platformGraphQlEndpoint = 'https://platform-test.example.com/graphql'
param alertEmail = 'merchant-integration-oncall@example.com'
param corsOrigins = ['https://merchant-bridge-test.example.com']
