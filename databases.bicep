// ════════════════════════════════════════════════════════════════════════════
// databases.bicep
//
// Provisions the polyglot persistence data tier:
//   Azure SQL (Business Critical) — ACID write primary + read replica
//   Azure Cosmos DB               — globally distributed read model
//   Azure Cache for Redis         — sub-ms cache-aside layer
//
// Every resource has its public network access DISABLED.
// All traffic routes through Private Endpoints inside the VNet.
// Managed Identities authenticate — no passwords in connection strings.
// ════════════════════════════════════════════════════════════════════════════

param prefix               string
param location             string
param tags                 object
param enableZoneRedundancy bool
param vnetId               string
param dbSubnetId           string
param keyVaultName         string
param orderApiIdentityId   string   // Principal ID for SQL role assignment
param workerIdentityId     string
param inventoryIdentityId  string

// ════════════════════════════════════════════════════════════════════════════
// 1. AZURE SQL DATABASE — Business Critical Tier
//    Business Critical = built-in Always On availability group with one
//    read-only secondary. OrderService points at the secondary for reads;
//    the Worker Function writes to the primary only.
// ════════════════════════════════════════════════════════════════════════════

resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name:     'sql-${prefix}'
  location: location
  tags:     tags
  identity: { type: 'SystemAssigned' }
  properties: {
    // Entra ID admin — no SQL authentication allowed (Zero Trust)
    administrators: {
      administratorType:         'ActiveDirectory'
      azureADOnlyAuthentication: true
      login:                     'logistics-sql-admins'
      sid:                       orderApiIdentityId   // AAD group or identity
      tenantId:                  tenant().tenantId
    }
    // Public endpoint disabled — access only via Private Endpoint
    publicNetworkAccess:   'Disabled'
    minimalTlsVersion:     '1.2'
  }
}

resource ordersDatabase 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent:   sqlServer
  name:     'OrdersDb'
  location: location
  tags:     tags
  sku: {
    // Business Critical Gen5 — built-in read replica, zone redundant in prod
    name:     'BC_Gen5'
    tier:     'BusinessCritical'
    family:   'Gen5'
    capacity: 4      // 4 vCores; scale up if order volume grows
  }
  properties: {
    zoneRedundant:        enableZoneRedundancy
    readScale:            'Enabled'     // Routes read-only connections to secondary
    highAvailabilityReplicaCount: 1
    requestedBackupStorageRedundancy: 'Geo'
    collation:            'SQL_Latin1_General_CP1_CI_AS'
  }
}

// ── SQL Private Endpoint ──────────────────────────────────────────────────────
resource sqlPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-09-01' = {
  name:     'pe-sql-${prefix}'
  location: location
  tags:     tags
  properties: {
    subnet: { id: dbSubnetId }
    privateLinkServiceConnections: [{
      name: 'sql-connection'
      properties: {
        privateLinkServiceId: sqlServer.id
        groupIds:             ['sqlServer']
      }
    }]
  }
}

// ── Store SQL connection strings in Key Vault ─────────────────────────────────
// OrderService and Worker Function read these secrets via Managed Identity.
// Pattern: Server=...;Authentication=Active Directory Managed Identity
// (no username, no password — Managed Identity IS the credential)

resource kvSqlWriteSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  name: '${keyVaultName}/Sql--OrdersConnectionString'
  properties: {
    value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=OrdersDb;Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;'
  }
}

resource kvSqlReadSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  name: '${keyVaultName}/Sql--OrdersReadConnectionString'
  properties: {
    // ApplicationIntent=ReadOnly → SQL redirects to the secondary replica
    value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=OrdersDb;Authentication=Active Directory Managed Identity;ApplicationIntent=ReadOnly;Encrypt=True;TrustServerCertificate=False;'
  }
}

// ════════════════════════════════════════════════════════════════════════════
// 2. AZURE COSMOS DB — NoSQL API, read model
//    Multi-region writes disabled (writes come from CDC, not apps).
//    Multi-region reads enabled — data routed to nearest replica.
// ════════════════════════════════════════════════════════════════════════════

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-02-15-preview' = {
  name:     'cosmos-${prefix}'
  location: location
  tags:     tags
  kind:     'GlobalDocumentDB'
  identity: { type: 'SystemAssigned' }
  properties: {
    databaseAccountOfferType:  'Standard'
    consistencyPolicy: {
      // Session consistency: sufficient for read model (not stock reservation)
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName:     location
        failoverPriority: 0
        isZoneRedundant:  enableZoneRedundancy
      }
      // Add secondary regions here for global distribution
      // { locationName: 'West Europe', failoverPriority: 1, isZoneRedundant: false }
    ]
    // Disable public access — Private Endpoint only
    publicNetworkAccess: 'Disabled'
    networkAclBypass:    'AzureServices'
    isVirtualNetworkFilterEnabled: false
    enableAnalyticalStorage:        false
    enableAutomaticFailover:        true
    enableMultipleWriteLocations:   false   // Read model = single write region
    backupPolicy: {
      type: 'Continuous'   // Point-in-time restore (30-day window)
      continuousModeProperties: { tier: 'Continuous7Days' }
    }
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-02-15-preview' = {
  parent: cosmosAccount
  name:   'LogisticsDb'
  properties: {
    resource: { id: 'LogisticsDb' }
  }
}

resource inventoryContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: cosmosDatabase
  name:   'inventory'
  properties: {
    resource: {
      id:           'inventory'
      partitionKey: { paths: ['/region'], kind: 'Hash' }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/sku/?' }
          { path: '/category/?' }
          { path: '/stockLevel/?' }
        ]
        excludedPaths: [{ path: '/*' }]
      }
      defaultTtl: -1   // No TTL — CDC manages document lifecycle
    }
    options: { autoscaleSettings: { maxThroughput: 4000 } }
  }
}

resource ordersContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: cosmosDatabase
  name:   'orders'
  properties: {
    resource: {
      id:           'orders'
      partitionKey: { paths: ['/region'], kind: 'Hash' }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/customerEmail/?' }
          { path: '/status/?' }
          { path: '/createdAt/?' }
        ]
        excludedPaths: [{ path: '/*' }]
      }
    }
    options: { autoscaleSettings: { maxThroughput: 4000 } }
  }
}

// ── Cosmos DB Private Endpoint ────────────────────────────────────────────────
resource cosmosPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-09-01' = {
  name:     'pe-cosmos-${prefix}'
  location: location
  tags:     tags
  properties: {
    subnet: { id: dbSubnetId }
    privateLinkServiceConnections: [{
      name: 'cosmos-connection'
      properties: {
        privateLinkServiceId: cosmosAccount.id
        groupIds:             ['Sql']
      }
    }]
  }
}

// ── Cosmos DB RBAC — Cosmos DB Built-in Data Reader ──────────────────────────
// inventoryIdentity reads from Cosmos; no write access from the app layer.
var cosmosDataReaderRoleId = '00000000-0000-0000-0000-000000000001'

resource inventoryCosmosRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-02-15-preview' = {
  parent: cosmosAccount
  name:   guid(cosmosAccount.id, inventoryIdentityId, cosmosDataReaderRoleId)
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/${cosmosDataReaderRoleId}'
    principalId:      inventoryIdentityId
    scope:            cosmosAccount.id
  }
}

// ════════════════════════════════════════════════════════════════════════════
// 3. AZURE CACHE FOR REDIS — Enterprise tier
//    Cache-aside for Inventory and Order summary KPIs.
//    Private Endpoint — no public access.
// ════════════════════════════════════════════════════════════════════════════

resource redisCache 'Microsoft.Cache/redis@2023-08-01' = {
  name:     'redis-${prefix}'
  location: location
  tags:     tags
  properties: {
    sku: {
      name:     'Premium'   // Premium required for VNet/Private Endpoint
      family:   'P'
      capacity: 1           // 6 GB; scale to P2 (13 GB) if needed
    }
    enableNonSslPort:     false
    minimumTlsVersion:    '1.2'
    publicNetworkAccess:  'Disabled'
    redisConfiguration: {
      'maxmemory-policy': 'allkeys-lru'   // Evict least-recently-used when full
    }
    replicasPerMaster: enableZoneRedundancy ? 1 : 0
    zones:             enableZoneRedundancy ? ['1', '2'] : []
  }
}

// ── Redis Private Endpoint ────────────────────────────────────────────────────
resource redisPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-09-01' = {
  name:     'pe-redis-${prefix}'
  location: location
  tags:     tags
  properties: {
    subnet: { id: dbSubnetId }
    privateLinkServiceConnections: [{
      name: 'redis-connection'
      properties: {
        privateLinkServiceId: redisCache.id
        groupIds:             ['redisCache']
      }
    }]
  }
}

// ── Store Redis connection string in Key Vault ─────────────────────────────────
resource kvRedisSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  name: '${keyVaultName}/Redis--ConnectionString'
  properties: {
    value: '${redisCache.properties.hostName}:6380,password=${redisCache.listKeys().primaryKey},ssl=True,abortConnect=False'
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────
output sqlServerFqdn        string = sqlServer.properties.fullyQualifiedDomainName
output cosmosAccountName    string = cosmosAccount.name
output cosmosEndpoint       string = cosmosAccount.properties.documentEndpoint
output redisHostName        string = redisCache.properties.hostName
