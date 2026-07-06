// ════════════════════════════════════════════════════════════════════════════
// security.bicep
//
// Zero Trust security layer:
//   1. One Managed Identity per microservice (principle of least privilege)
//   2. Key Vault for all secrets — no credentials in environment variables
//   3. Private Endpoint for Key Vault — no public internet access
//   4. RBAC role assignments wired at deploy time (no portal clicking)
// ════════════════════════════════════════════════════════════════════════════

param prefix                string
param location              string
param tags                  object
param vnetId                string
param keyVaultSubnetId      string
param ciCdPrincipalObjectId string  // GitHub Actions OIDC principal

// ── Managed Identities ────────────────────────────────────────────────────────
// One identity per service = granular RBAC, no shared credentials.

resource webAppIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name:     'id-webapp-${prefix}'
  location: location
  tags:     tags
}

resource orderApiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name:     'id-order-api-${prefix}'
  location: location
  tags:     tags
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name:     'id-worker-${prefix}'
  location: location
  tags:     tags
}

resource inventoryIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name:     'id-inventory-${prefix}'
  location: location
  tags:     tags
}

// ── Key Vault ─────────────────────────────────────────────────────────────────
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name:     'kv-${prefix}'
  location: location
  tags:     tags
  properties: {
    sku: { family: 'A', name: 'premium' }  // Premium: HSM-backed keys
    tenantId: tenant().tenantId

    // ── Public access completely disabled ──────────────────────────────────
    // All traffic must arrive via the Private Endpoint inside the VNet.
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass:        'AzureServices'
      defaultAction: 'Deny'
      ipRules:       []
      virtualNetworkRules: []
    }

    // ── RBAC mode (not legacy Access Policies) ─────────────────────────────
    enableRbacAuthorization:     true
    enableSoftDelete:            true
    softDeleteRetentionInDays:   90
    enablePurgeProtection:       true
  }
}

// ── Key Vault Private Endpoint ────────────────────────────────────────────────
resource kvPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-09-01' = {
  name:     'pe-kv-${prefix}'
  location: location
  tags:     tags
  properties: {
    subnet: { id: keyVaultSubnetId }
    privateLinkServiceConnections: [{
      name: 'kv-connection'
      properties: {
        privateLinkServiceId: keyVault.id
        groupIds:             ['vault']
      }
    }]
  }
}

// ── RBAC Role Assignments — Key Vault ─────────────────────────────────────────
// Key Vault Secrets User = read secrets only (not manage)
var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource webAppKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope:  keyVault
  name:   guid(keyVault.id, webAppIdentity.id, kvSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId:      webAppIdentity.properties.principalId
    principalType:    'ServicePrincipal'
  }
}

resource orderApiKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope:  keyVault
  name:   guid(keyVault.id, orderApiIdentity.id, kvSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId:      orderApiIdentity.properties.principalId
    principalType:    'ServicePrincipal'
  }
}

resource workerKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope:  keyVault
  name:   guid(keyVault.id, workerIdentity.id, kvSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId:      workerIdentity.properties.principalId
    principalType:    'ServicePrincipal'
  }
}

resource inventoryKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope:  keyVault
  name:   guid(keyVault.id, inventoryIdentity.id, kvSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId:      inventoryIdentity.properties.principalId
    principalType:    'ServicePrincipal'
  }
}

// CI/CD pipeline needs Key Vault Secrets Officer to write secrets during deploy
var kvSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource ciCdKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name:  guid(keyVault.id, ciCdPrincipalObjectId, kvSecretsOfficerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsOfficerRoleId)
    principalId:      ciCdPrincipalObjectId
    principalType:    'ServicePrincipal'
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────
output keyVaultName    string = keyVault.name
output keyVaultUri     string = keyVault.properties.vaultUri
output keyVaultId      string = keyVault.id

// Resource IDs (for compute module to assign identities to Container Apps)
output webAppIdentityId      string = webAppIdentity.id
output orderApiIdentityId    string = orderApiIdentity.id
output workerIdentityId      string = workerIdentity.id
output inventoryIdentityId   string = inventoryIdentity.id

// Principal IDs (for RBAC role assignments in other modules)
output webAppIdentityPrincipalId      string = webAppIdentity.properties.principalId
output orderApiIdentityPrincipalId    string = orderApiIdentity.properties.principalId
output workerIdentityPrincipalId      string = workerIdentity.properties.principalId
output inventoryIdentityPrincipalId   string = inventoryIdentity.properties.principalId
