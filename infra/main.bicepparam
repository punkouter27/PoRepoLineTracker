using 'main.bicep'

// The live resources (read with `az resource list -g PoRepoLineTracker` and `-g PoShared`).
param location = 'eastus2'
param resourceGroupName = 'PoRepoLineTracker'
param sharedResourceGroupName = 'PoShared'
param storageAccountName = 'stporepolinetracker2'
param appInsightsName = 'poappideinsights8f9c9a4e'
param webAppName = 'app-porepolinetracker2'
param appServicePlanName = 'asp-porepolinetracker-f1'
param keyVaultName = 'kv-poshared2'
