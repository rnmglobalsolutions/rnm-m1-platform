using './main.bicep'

param environmentName = 'dev'
param location = readEnvironmentVariable('AZURE_LOCATION', 'eastus')
param sendGridApiKeySecretName = 'rnm-dev-sendgrid-api-key'
param operationsAlertEmail = readEnvironmentVariable('RNM_OPERATIONS_ALERT_EMAIL', '')
