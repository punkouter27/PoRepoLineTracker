targetScope = 'subscription'

@minLength(1)
@description('Primary location for all resources')
param location string = 'eastus2'

// Naming convention: Po{SolutionName} prefix. The live app + storage reside in the
// 'PoRepoLineTracker' resource group (not 'rg-...-prod'); keep Bicep aligned to reality
// so a provision does not create a duplicate, empty resource group.
var resourceGroupName = 'PoRepoLineTracker'
var sharedResourceGroupName = 'PoShared'
var storageAccountName = 'stporepolinetracker2'  // Existing storage account in PoRepoLineTracker RG
var appInsightsName = 'poappideinsights8f9c9a4e'  // Shared App Insights in PoShared RG
var webAppName = 'app-porepolinetracker2'  // App Service in PoRepoLineTracker RG
// Dedicated F1 (Free) plan, created and owned by this template in the PoRepoLineTracker RG.
// Previously this referenced the shared B1 in PoShared; the app now runs on its own free plan
// so it costs nothing and cannot contend with the other Po* sites. See the F1 constraints
// documented on the plan resource in resources.bicep — Always On is not one of them.
var appServicePlanName = 'asp-porepolinetracker-f1'
var keyVaultName = 'kv-poshared2'  // Existing Key Vault in PoShared RG

// The app resource group — created here if it does not exist.
resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: resourceGroupName
  location: location
}

// Deploy resources into the app resource group
module resources 'resources.bicep' = {
  name: 'resources'
  scope: rg
  params: {
    // West US 2, NOT the East US 2 the storage account uses. This subscription has zero Free
    // tier quota in East US 2 — preflight fails with SubscriptionIsOverQuotaForSku and
    // "Current Limit (Total VMs): 0" — and every other F1 plan on the subscription is West US 2
    // for the same reason. The storage account stays in East US 2; the cross-region hop is the
    // price of the free plan, and it is what the previous shared B1 did anyway.
    webAppLocation: 'westus2'
    storageAccountName: storageAccountName
    appInsightsName: appInsightsName
    webAppName: webAppName
    appServicePlanName: appServicePlanName
    keyVaultName: keyVaultName
    sharedResourceGroupName: sharedResourceGroupName
  }
}

// Outputs
@description('Azure region where resources were deployed')
output AZURE_LOCATION string = location

@description('Name of the resource group containing all resources')
output AZURE_RESOURCE_GROUP string = rg.name

@description('Name of the Azure Storage Account')
output AZURE_STORAGE_ACCOUNT_NAME string = resources.outputs.storageAccountName

@description('Application Insights connection string for telemetry configuration')
output APPLICATIONINSIGHTS_CONNECTION_STRING string = resources.outputs.appInsightsConnectionString

@description('Public URL of the deployed application')
output SERVICE_API_URL string = resources.outputs.webAppUrl
