// Grants the web app's managed identity secret get/list on the shared Key Vault.
//
// Its own module because kv-poshared2 lives in the PoShared resource group, and a cross-RG
// child resource needs a scoped module.
//
// This used to be a comment in resources.bicep telling a human to go and do it by hand. Nobody
// did: the site was recreated, got a fresh principal with no policy, and Program.cs — which
// reads KeyVault__Uri during startup — threw on every boot. App Service restarted it 35 times
// and the F1 plan cut the site off at WPStopRequests 15/15, which presents as HTTP 403
// "Web App - Unavailable" rather than anything mentioning Key Vault.
//
// kv-poshared2 runs in access-policy mode (enableRbacAuthorization: false), so this is a policy
// and not a role assignment. Name 'add' is the additive operation — it appends to the vault's
// existing policies rather than replacing them, which matters because ~70 other identities
// depend on the ones already there.

@description('Name of the Key Vault in this resource group')
param keyVaultName string

@description('Principal ID of the identity to grant secret get/list')
param principalId string

@description('Tenant ID that owns the principal')
param tenantId string

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource accessPolicy 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01' = {
  parent: keyVault
  name: 'add'
  properties: {
    accessPolicies: [
      {
        tenantId: tenantId
        objectId: principalId
        permissions: {
          secrets: ['get', 'list']
        }
      }
    ]
  }
}
