@description('Location for the Web App (must match the App Service Plan location)')
param webAppLocation string

@description('Name of the Azure Storage Account for Table Storage')
param storageAccountName string

@description('Name of the Application Insights instance')
param appInsightsName string

@description('Name of the App Service (Web App)')
param webAppName string

@description('Name of the App Service Plan that the web app binds to. Created by this template in this resource group on the F1 (Free) Linux tier.')
param appServicePlanName string

@description('Name of the Key Vault in the PoShared resource group')
param keyVaultName string

@description('Name of the PoShared resource group containing shared services and the App Service Plan')
param sharedResourceGroupName string

// ═════════════════════════════════════════════════════════════════════════════════════════
// The App Service Plan (F1 Free, Linux) and the App Service are both created in THIS resource
// group (PoRepoLineTracker), alongside the storage account. App Insights, Key Vault, and Log
// Analytics remain in the shared PoShared RG and are referenced as existing.
//
// A plan and the sites on it must share a region, and Azure will not re-parent a site across
// regions or home stamps (extended error 59602 on a cross-stamp serverFarmId patch). Moving
// this app to a different plan therefore means deleting and recreating the site, not editing
// serverFarmId — worth knowing before changing appServicePlanName.
// ═════════════════════════════════════════════════════════════════════════════════════════

// ─────────────────────────────────────────────
// Existing resources (pre-created in this RG)
// ─────────────────────────────────────────────

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

// Shared resources in PoShared RG

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
  scope: resourceGroup(sharedResourceGroupName)
}

// ─────────────────────────────────────────────
// Reference Key Vault in PoShared resource group
// ─────────────────────────────────────────────

resource sharedKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
  scope: resourceGroup(sharedResourceGroupName)
}

// ─────────────────────────────────────────────
// App Service Plan — F1 (Free), Linux, dedicated to this app.
//
// `reserved: true` is what makes a plan Linux. It is not inferred from the site's
// linuxFxVersion: omit it and Azure provisions a Windows plan, and the DOTNETCORE|10.0 site
// then fails to bind to it.
// ─────────────────────────────────────────────

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: webAppLocation
  sku: {
    name: 'F1'
    tier: 'Free'
  }
  properties: {
    reserved: true
  }
}

// ─────────────────────────────────────────────
// App Service (Web App) — uses system-assigned managed identity
// Secrets pulled from PoShared Key Vault at runtime via DefaultAzureCredential
// Runs on the F1 (Free) Linux plan created above, in this resource group
// ─────────────────────────────────────────────

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: webAppLocation
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'azd-service-name': 'api'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      // startup.sh ships in the code package and installs git (GitClient shells out to it)
      // before exec-ing the app. Bicep is the single owner of this value — a workflow step
      // used to reset it after every provision because this line said `dotnet …dll`, which
      // flip-flopped the startup command (and restarted the site) twice per deploy.
      // Until the first code deploy lands startup.sh the site cannot start; the code deploy
      // in the same workflow run follows immediately and restarts it with the file present.
      appCommandLine: '/home/site/wwwroot/startup.sh'
      // MUST stay false on F1: the Free tier does not offer Always On, and ARM rejects the
      // whole site write with SiteWithAlwaysOnNotSupportedForOffering rather than ignoring it.
      // The cost is a cold start on the first hit after the idle unload.
      alwaysOn: false
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        // Key Vault URI — app reads secrets via DefaultAzureCredential at startup.
        // MUST be 'KeyVault__Uri' to bind to config key 'KeyVault:Uri' that Program.cs reads.
        {
          name: 'KeyVault__Uri'
          value: sharedKeyVault.properties.vaultUri
        }
        // Explicit Production environment (App Service defaults to Production, but be explicit).
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        // Table Storage — use DefaultAzureCredential, not connection string keys
        {
          name: 'AzureTableStorage__ServiceUrl'
          value: storageAccount.properties.primaryEndpoints.table
        }
        // Non-secret table names
        {
          name: 'AzureTableStorage__RepositoryTableName'
          value: 'PoRepoLineTrackerRepositories'
        }
        {
          name: 'AzureTableStorage__CommitLineCountTableName'
          value: 'PoRepoLineTrackerCommitLineCounts'
        }
        {
          name: 'AzureTableStorage__UserTableName'
          value: 'PoRepoLineTrackerUsers'
        }
        {
          name: 'AzureTableStorage__UserPreferencesTableName'
          value: 'PoRepoLineTrackerUserPreferences'
        }
        {
          name: 'GitHub__LocalReposPath'
          value: '/home/LocalRepos'
        }
        {
          name: 'GitHub__CallbackPath'
          value: '/signin-github'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'ASPNETCORE_URLS'
          value: 'http://+:8080'
        }
      ]
    }
  }
}

// Storage Table Data Contributor for the web app identity. Via a module because the assignment
// name has to be seeded with principalId, which Bicep cannot resolve inline — see storage-role.bicep.
module webAppStorageTableDataContributor 'storage-role.bicep' = {
  name: 'storage-table-role'
  params: {
    storageAccountId: storageAccount.id
    storageAccountName: storageAccount.name
    principalId: webApp.identity.principalId
  }
}

// Secret get/list on the shared Key Vault for the web app identity. Without it the app cannot
// start at all — see keyvault-access.bicep for why this is a module and what the failure looks
// like. Storage table data-plane access is separate, and uses RBAC rather than a vault policy.
module webAppKeyVaultAccess 'keyvault-access.bicep' = {
  name: 'keyvault-access'
  scope: resourceGroup(sharedResourceGroupName)
  params: {
    keyVaultName: keyVaultName
    principalId: webApp.identity.principalId
    tenantId: subscription().tenantId
  }
}

// ─────────────────────────────────────────────
// Availability test — ping /health every 5 min from 3 US regions and report to the
// shared App Insights, so an outage (like the recent home-page 5xx) is caught
// proactively instead of via user reports. Deployed to PoShared (same RG + region as
// the App Insights component, which the platform requires). Wire an action group later.
// ─────────────────────────────────────────────

module healthAvailabilityTest 'availability-test.bicep' = {
  name: 'availability-test'
  scope: resourceGroup(sharedResourceGroupName)
  params: {
    appInsightsId: appInsights.id
    location: appInsights.location
    webAppHostName: webApp.properties.defaultHostName
    webAppName: webAppName
  }
}

// Outputs
@description('Name of the deployed Storage Account')
output storageAccountName string = storageAccount.name

@description('Application Insights connection string for telemetry')
output appInsightsConnectionString string = appInsights.properties.ConnectionString

@description('Public URL of the deployed Web App')
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'

@description('Web App managed identity principal ID (use to grant Key Vault access)')
output webAppPrincipalId string = webApp.identity.principalId
