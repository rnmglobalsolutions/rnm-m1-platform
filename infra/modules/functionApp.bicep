targetScope = 'resourceGroup'

@description('Function App name.')
param name string

@description('Hosting plan name.')
param planName string

@description('Azure region for the Function App.')
param location string

@description('Deployment environment name.')
param environmentName string

@secure()
@description('Storage account connection string for Azure Functions runtime state.')
param storageAccountConnectionString string

@description('Blob endpoint for Function App deployment package storage.')
param storageBlobEndpoint string

@description('Blob container used by Flex Consumption deployment storage.')
param deploymentStorageContainerName string = 'function-deployments'

@description('Application Insights connection string.')
param applicationInsightsConnectionString string

@description('Key Vault URI used by the app for secret retrieval.')
param keyVaultUri string

@description('Config root path used by the Function App.')
param configRoot string = '/home/site/wwwroot/config'

@description('Internal API key secret name in Key Vault.')
param internalApiKeySecretName string = 'rnm-internal-api-key'

@description('SendGrid API key secret name in Key Vault. The secret value is created outside Bicep.')
param sendGridApiKeySecretName string = 'sendgrid-api-key'

@description('Additional application settings.')
param additionalAppSettings object = {}

@description('Maximum Flex Consumption instance count.')
param maximumInstanceCount int = 20

@allowed([
  512
  2048
  4096
])
@description('Flex Consumption instance memory size in MB. Dev defaults to the smallest cost-conscious size.')
param instanceMemoryMB int = 512

@description('Resource tags.')
param tags object = {}

var baseAppSettings = {
  APPLICATIONINSIGHTS_CONNECTION_STRING: applicationInsightsConnectionString
  AzureWebJobsStorage: storageAccountConnectionString
  FUNCTIONS_EXTENSION_VERSION: '~4'
  RNM_CONFIG_ROOT: configRoot
  RNM_ENVIRONMENT: environmentName
  RNM_INTERNAL_API_KEY_SECRET_NAME: '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${internalApiKeySecretName}/)'
  RNM_KEY_VAULT_URI: keyVaultUri
  RNM_VAPI_WEBHOOK_JSON_MAX_DEPTH: '32'
  RNM_VAPI_WEBHOOK_MAX_BODY_BYTES: '262144'
  SENDGRID_API_KEY: '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${sendGridApiKeySecretName}/)'
}
var mergedAppSettings = union(baseAppSettings, additionalAppSettings)

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    httpsOnly: true
    serverFarmId: plan.id
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageBlobEndpoint}${deploymentStorageContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
    }
    siteConfig: {
      alwaysOn: false
      appSettings: [
        for appSetting in items(mergedAppSettings): {
          name: appSetting.key
          value: appSetting.value
        }
      ]
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      cors: {
        allowedOrigins: []
        supportCredentials: false
      }
    }
  }
}

output functionAppId string = functionApp.id
output functionAppName string = functionApp.name
output principalId string = functionApp.identity.principalId
output defaultHostName string = functionApp.properties.defaultHostName
