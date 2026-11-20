// Storage Table Data Contributor for the web app's managed identity.
//
// This lives in its own module solely because of a Bicep restriction: a resource `name` must be
// computable before the deployment starts, and `webApp.identity.principalId` is not — it exists
// only after the site is created. Passing it in as a module PARAMETER makes it a plain string by
// the time this template is evaluated, so it can seed the guid().
//
// Seeding on principalId matters because a role assignment's principal is immutable. The obvious
// alternative, guid(scope, webApp.id, role), is derived from the site NAME and so is byte-identical
// after a delete-and-recreate — at which point the new identity collides with the old assignment
// and ARM fails the whole deployment with RoleAssignmentUpdateNotPermitted.

@description('Resource ID of the storage account to grant table access on')
param storageAccountId string

@description('Name of the storage account to grant table access on (must be in this resource group)')
param storageAccountName string

@description('Principal ID of the web app managed identity')
param principalId string

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

resource storageTableDataContributorRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' existing = {
  // Built-in role: Storage Table Data Contributor
  name: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  scope: subscription()
}

resource assignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountId, principalId, storageTableDataContributorRole.id)
  scope: storageAccount
  properties: {
    roleDefinitionId: storageTableDataContributorRole.id
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
