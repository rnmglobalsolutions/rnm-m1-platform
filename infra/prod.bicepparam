using './main.bicep'

param environmentName = 'prod'
param location = readEnvironmentVariable('AZURE_LOCATION', 'eastus')
param sendGridApiKeySecretName = 'rnm-prod-sendgrid-api-key'
