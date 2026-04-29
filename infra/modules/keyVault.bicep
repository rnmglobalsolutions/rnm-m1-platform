targetScope = 'resourceGroup'

@description('Key Vault name. Must be globally unique and 3-24 characters.')
param name string

@description('Azure region for Key Vault.')
param location string

@description('Resource tags.')
param tags object = {}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enablePurgeProtection: true
    enableRbacAuthorization: true
    enableSoftDelete: true
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    publicNetworkAccess: 'Enabled'
    accessPolicies: []
  }
}

output keyVaultId string = keyVault.id
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
