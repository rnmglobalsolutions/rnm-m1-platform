using './main.bicep'

param environmentName = 'staging'
param location = readEnvironmentVariable('AZURE_LOCATION', 'eastus')
