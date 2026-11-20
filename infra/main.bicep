targetScope = 'subscription'

@minLength(1)
@description('Primary location for all resources')
param location string = 'eastus2'

// Names of the resources the live app uses today (values in main.bicepparam). Azure naming standard:
// App Service app-<repo lowercase>, per-app resources <type>-<repo lowercase>, shared things in PoShared.
// This app predates it: the live names carry a "2" (app-porepolinetracker2, stporepolinetracker2) and
// the resource group is "PoRepoLineTracker". Renaming means recreating the site and the storage
// account (and the GitHub OAuth callback URL), so the template describes the live names.
@description('Resource group holding the app, its plan and its storage account')
param resourceGroupName string

@description('Resource group holding the shared Key Vault and Application Insights')
param sharedResourceGroupName string

@description('Storage account (Table Storage) in the app resource group')
param storageAccountName string

@description('Shared Application Insights component in the PoShared resource group')
param appInsightsName string

@description('App Service (Linux) in the app resource group')
param webAppName string

@description('Dedicated F1 (Free) Linux plan, created and owned by this template. Always On is not available on F1.')
param appServicePlanName string

@description('Shared Key Vault in the PoShared resource group')
param keyVaultName string

// The app resource group — created here if it does not exist.
resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: resourceGroupName
  location: location
}

// Deploy resources into the app resource group
module resources 'modules/resources.bicep' = {
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
