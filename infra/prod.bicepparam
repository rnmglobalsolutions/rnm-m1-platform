using './main.bicep'

param environmentName = 'prod'
param location = readEnvironmentVariable('AZURE_LOCATION', 'eastus')
param sendGridApiKeySecretName = 'rnm-prod-sendgrid-api-key'
param operationsAlertEmail = readEnvironmentVariable('RNM_OPERATIONS_ALERT_EMAIL', '')
