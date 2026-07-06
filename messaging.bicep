// ════════════════════════════════════════════════════════════════════════════
// messaging.bicep — Azure Service Bus (Premium tier)
//
// Why Premium (not Standard)?
//   Standard lacks: Private Endpoints, Message Sessions (FIFO), dedicated
//   capacity (no noisy neighbour), and zone redundancy.
//   Premium provides all of these — required for enterprise FIFO ordering
//   and full VNet isolation.
// ════════════════════════════════════════════════════════════════════════════

param prefix             string
param location           string
param tags               object
param vnetId             string
param messagingSubnetId  string
param keyVaultName       string
param orderApiIdentityId string   // Principal ID
param workerIdentityId   string
param webAppIdentityId   string

// ── Service Bus Namespace ─────────────────────────────────────────────────────
resource sbNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name:     'sb-${prefix}'
  location: location
  tags:     tags
  sku: {
    name:     'Premium'
    tier:     'Premium'
    capacity: 1      // 1 messaging unit; scale to 2-4 for high throughput
  }
  identity: { type: 'SystemAssigned' }
  properties: {
    publicNetworkAccess:   'Disabled'    // Private Endpoint only
    minimumTlsVersion:     '1.2'
    zoneRedundant:         true          // Premium supports zone redundancy
    premiumMessagingPartitions: 1
    disableLocalAuth:      true          // Force Entra ID auth — no SAS keys
  }
}

// ── FIFO Order Queue ──────────────────────────────────────────────────────────
resource orderQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: sbNamespace
  name:   'orders-fifo-queue'
  properties: {
    // ── FIFO via Message Sessions ──────────────────────────────────────────
    // requiresSession: true means every message MUST carry a SessionId.
    // The Worker Function acquires an exclusive lock per session (CustomerEmail),
    // guaranteeing messages for one customer are processed exactly in order.
    requiresSession: true

    // ── Duplicate detection ────────────────────────────────────────────────
    // 10-minute window. If the OrderService retries within this window,
    // Service Bus silently drops the duplicate without delivering it to the Worker.
    requiresDuplicateDetection:          true
    duplicateDetectionHistoryTimeWindow: 'PT10M'

    // ── Dead-letter on expiry ──────────────────────────────────────────────
    // Messages that aren't processed within 7 days move to the DLQ.
    deadLetteringOnMessageExpiration: true
    messageTimeToLive:                'P7D'

    // ── Max delivery attempts before dead-lettering ────────────────────────
    maxDeliveryCount: 5

    // ── Lock duration: how long the Worker holds the message lock ─────────
    // Must exceed the maximum expected processing time (SQL write + stock check).
    lockDuration: 'PT5M'

    maxSizeInMegabytes: 1024
  }
}

// ── Service Bus Private Endpoint ──────────────────────────────────────────────
resource sbPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-09-01' = {
  name:     'pe-sb-${prefix}'
  location: location
  tags:     tags
  properties: {
    subnet: { id: messagingSubnetId }
    privateLinkServiceConnections: [{
      name: 'sb-connection'
      properties: {
        privateLinkServiceId: sbNamespace.id
        groupIds:             ['namespace']
      }
    }]
  }
}

// ── RBAC — Azure Service Bus Data Sender ──────────────────────────────────────
// OrderService (API) and WebApp can SEND to the queue.
// Worker Function can only RECEIVE.
var sbDataSenderRoleId   = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
var sbDataReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'

resource orderApiSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: sbNamespace
  name:  guid(sbNamespace.id, orderApiIdentityId, sbDataSenderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sbDataSenderRoleId)
    principalId:      orderApiIdentityId
    principalType:    'ServicePrincipal'
  }
}

resource webAppSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: sbNamespace
  name:  guid(sbNamespace.id, webAppIdentityId, sbDataSenderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sbDataSenderRoleId)
    principalId:      webAppIdentityId
    principalType:    'ServicePrincipal'
  }
}

resource workerReceiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: sbNamespace
  name:  guid(sbNamespace.id, workerIdentityId, sbDataReceiverRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sbDataReceiverRoleId)
    principalId:      workerIdentityId
    principalType:    'ServicePrincipal'
  }
}

// ── Store Service Bus namespace in Key Vault (apps read from KV) ──────────────
resource kvSbSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  name: '${keyVaultName}/ServiceBus--FullyQualifiedNamespace'
  properties: {
    value: '${sbNamespace.name}.servicebus.windows.net'
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────
output serviceBusNamespace string = '${sbNamespace.name}.servicebus.windows.net'
output orderQueueName      string = orderQueue.name
output serviceBusId        string = sbNamespace.id
