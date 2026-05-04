targetScope = 'resourceGroup'

@description('Deployment environment name.')
@allowed([
  'dev'
  'staging'
  'prod'
])
param environmentName string

@description('Azure region for M1 resources.')
param location string = resourceGroup().location

@description('Short resource prefix used in names.')
param resourcePrefix string = 'rnm-m1'

@description('Optional object id for break-glass/admin secret management access.')
param keyVaultAdministratorObjectId string = ''

@description('Config root path used by the Function App.')
param configRoot string = '/home/site/wwwroot/config'

@description('Internal API key secret name in Key Vault.')
param internalApiKeySecretName string = 'rnm-internal-api-key'

@description('SendGrid API key secret name in Key Vault. Bicep references this secret but does not create its value.')
param sendGridApiKeySecretName string = 'sendgrid-api-key'

@description('Additional Function App settings. Values must be non-sensitive.')
param additionalFunctionAppSettings object = {}

var normalizedPrefix = toLower(replace(resourcePrefix, '_', '-'))
var suffix = uniqueString(resourceGroup().id, environmentName)
var resourceBaseName = '${normalizedPrefix}-${environmentName}-${suffix}'
var tags = {
  application: 'rnm-platform'
  environment: environmentName
  phase: 'm1'
}

var storageNameSeed = toLower(replace('${normalizedPrefix}${environmentName}${suffix}', '-', ''))
var storageAccountName = substring(storageNameSeed, 0, min(length(storageNameSeed), 24))
var keyVaultName = substring(resourceBaseName, 0, min(length(resourceBaseName), 24))
var appInsightsName = '${resourceBaseName}-appi'
var logAnalyticsWorkspaceName = '${resourceBaseName}-law'
var planName = '${resourceBaseName}-plan'
var functionAppName = '${resourceBaseName}-func'

module storageAccount 'modules/storageAccount.bicep' = {
  name: 'storage-${environmentName}'
  params: {
    name: storageAccountName
    location: location
    tags: tags
  }
}

module appInsights 'modules/appInsights.bicep' = {
  name: 'app-insights-${environmentName}'
  params: {
    name: appInsightsName
    workspaceName: logAnalyticsWorkspaceName
    location: location
    tags: tags
  }
}

module keyVault 'modules/keyVault.bicep' = {
  name: 'key-vault-${environmentName}'
  params: {
    name: keyVaultName
    location: location
    tags: tags
  }
}

module functionApp 'modules/functionApp.bicep' = {
  name: 'function-app-${environmentName}'
  params: {
    name: functionAppName
    planName: planName
    location: location
    environmentName: environmentName
    storageAccountConnectionString: storageAccount.outputs.primaryConnectionString
    storageBlobEndpoint: storageAccount.outputs.primaryBlobEndpoint
    deploymentStorageContainerName: storageAccount.outputs.deploymentContainerName
    applicationInsightsConnectionString: appInsights.outputs.connectionString
    keyVaultUri: keyVault.outputs.keyVaultUri
    configRoot: configRoot
    internalApiKeySecretName: internalApiKeySecretName
    sendGridApiKeySecretName: sendGridApiKeySecretName
    additionalAppSettings: additionalFunctionAppSettings
    tags: tags
  }
}

resource deployedKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource deployedStorageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource keyVaultSecretsUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVaultName, functionAppName, 'Key Vault Secrets User')
  scope: deployedKeyVault
  properties: {
    principalId: functionApp.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

resource storageBlobDataOwnerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountName, functionAppName, 'Storage Blob Data Owner')
  scope: deployedStorageAccount
  properties: {
    principalId: functionApp.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  }
}

resource keyVaultSecretsOfficerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(keyVaultAdministratorObjectId)) {
  name: guid(keyVaultName, keyVaultAdministratorObjectId, 'Key Vault Secrets Officer')
  scope: deployedKeyVault
  properties: {
    principalId: keyVaultAdministratorObjectId
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
  }
}

output functionAppName string = functionApp.outputs.functionAppName
output functionAppDefaultHostName string = functionApp.outputs.defaultHostName
output keyVaultName string = keyVault.outputs.keyVaultName
output keyVaultUri string = keyVault.outputs.keyVaultUri
output appInsightsName string = appInsights.outputs.appInsightsName
output storageAccountName string = storageAccount.outputs.storageAccountName
