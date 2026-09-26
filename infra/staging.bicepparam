using './main.bicep'

param environmentName = 'staging'
param location = readEnvironmentVariable('AZURE_LOCATION', 'eastus')
param sendGridApiKeySecretName = 'rnm-sendgrid-api-key'
param activeTenants = ''
param operationsAlertEmail = readEnvironmentVariable('RNM_OPERATIONS_ALERT_EMAIL', '')
param keyVaultAdministratorObjectId = readEnvironmentVariable('RNM_KEY_VAULT_ADMIN_OBJECT_ID', '')
