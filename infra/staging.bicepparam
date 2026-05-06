using './main.bicep'

param environmentName = 'staging'
param location = readEnvironmentVariable('AZURE_LOCATION', 'eastus')
param sendGridApiKeySecretName = 'rnm-staging-sendgrid-api-key'
param allowedCorsOrigins = [
  'https://www.rnmglobalsolutions.com'
  'https://rnmglobalsolutions.com'
]
