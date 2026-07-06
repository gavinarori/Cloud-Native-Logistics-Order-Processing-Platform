// ════════════════════════════════════════════════════════════════════════════
// networking.bicep
//
// Creates the Virtual Network with four dedicated subnets and the Private
// DNS zones needed for Private Endpoint name resolution.
//
// Subnet layout:
//   compute-subnet     10.0.1.0/24  Container Apps, Azure Functions
//   db-subnet          10.0.2.0/24  SQL, Cosmos DB, Redis private endpoints
//   messaging-subnet   10.0.3.0/24  Service Bus private endpoint
//   apim-subnet        10.0.4.0/24  API Management (Premium, VNet injection)
//   kv-subnet          10.0.5.0/24  Key Vault private endpoint
// ════════════════════════════════════════════════════════════════════════════

param prefix   string
param location string
param tags     object

// ── Virtual Network ───────────────────────────────────────────────────────────
resource vnet 'Microsoft.Network/virtualNetworks@2023-09-01' = {
  name:     'vnet-${prefix}'
  location: location
  tags:     tags
  properties: {
    addressSpace: { addressPrefixes: ['10.0.0.0/16'] }
    subnets: [
      {
        name: 'compute-subnet'
        properties: {
          addressPrefix: '10.0.1.0/24'
          // Delegation required for Container Apps Environment injection
          delegations: [{
            name: 'Microsoft.App.environments'
            properties: { serviceName: 'Microsoft.App/environments' }
          }]
          privateEndpointNetworkPolicies:    'Disabled'
          privateLinkServiceNetworkPolicies: 'Enabled'
        }
      }
      {
        name: 'db-subnet'
        properties: {
          addressPrefix: '10.0.2.0/24'
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
      {
        name: 'messaging-subnet'
        properties: {
          addressPrefix: '10.0.3.0/24'
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
      {
        name: 'apim-subnet'
        properties: {
          addressPrefix: '10.0.4.0/24'
          // APIM Premium requires its own NSG (attached below)
        }
      }
      {
        name: 'kv-subnet'
        properties: {
          addressPrefix: '10.0.5.0/24'
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

// ── NSG for APIM subnet ───────────────────────────────────────────────────────
// Azure API Management Premium (VNet injection) requires specific inbound rules.
resource apimNsg 'Microsoft.Network/networkSecurityGroups@2023-09-01' = {
  name:     'nsg-apim-${prefix}'
  location: location
  tags:     tags
  properties: {
    securityRules: [
      {
        name: 'AllowAPIMManagement'
        properties: {
          priority:                 100
          protocol:                 'Tcp'
          access:                   'Allow'
          direction:                'Inbound'
          sourceAddressPrefix:      'ApiManagement'
          sourcePortRange:          '*'
          destinationAddressPrefix: 'VirtualNetwork'
          destinationPortRange:     '3443'
        }
      }
      {
        name: 'AllowAzureLoadBalancer'
        properties: {
          priority:                 110
          protocol:                 'Tcp'
          access:                   'Allow'
          direction:                'Inbound'
          sourceAddressPrefix:      'AzureLoadBalancer'
          sourcePortRange:          '*'
          destinationAddressPrefix: 'VirtualNetwork'
          destinationPortRange:     '6390'
        }
      }
      {
        name: 'AllowHTTPS'
        properties: {
          priority:                 200
          protocol:                 'Tcp'
          access:                   'Allow'
          direction:                'Inbound'
          sourceAddressPrefix:      'Internet'
          sourcePortRange:          '*'
          destinationAddressPrefix: 'VirtualNetwork'
          destinationPortRange:     '443'
        }
      }
    ]
  }
}

// Associate NSG with APIM subnet
resource apimSubnetNsgAssociation 'Microsoft.Network/virtualNetworks/subnets@2023-09-01' = {
  parent: vnet
  name:   'apim-subnet'
  properties: {
    addressPrefix: '10.0.4.0/24'
    networkSecurityGroup: { id: apimNsg.id }
  }
}

// ── Private DNS Zones ─────────────────────────────────────────────────────────
// Required for Private Endpoints — without these, FQDN resolution falls back
// to public IP addresses, defeating the point of Private Endpoints entirely.

var privateDnsZones = [
  'privatelink.database.windows.net'          // Azure SQL
  'privatelink.documents.azure.com'            // Cosmos DB
  'privatelink.redis.cache.windows.net'        // Redis
  'privatelink.servicebus.windows.net'         // Service Bus
  'privatelink.vaultcore.azure.net'            // Key Vault
]

resource dnsZones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in privateDnsZones: {
  name:     zone
  location: 'global'
  tags:     tags
}]

// Link each DNS zone to the VNet so Container Apps and Functions can resolve
// private endpoint FQDNs without leaving the VNet.
resource dnsZoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, i) in privateDnsZones: {
  parent:   dnsZones[i]
  name:     'link-${prefix}'
  location: 'global'
  tags:     tags
  properties: {
    virtualNetwork:      { id: vnet.id }
    registrationEnabled: false
  }
}]

// ── Outputs ───────────────────────────────────────────────────────────────────
output vnetId           string = vnet.id
output computeSubnetId  string = vnet.properties.subnets[0].id
output dbSubnetId       string = vnet.properties.subnets[1].id
output messagingSubnetId string = vnet.properties.subnets[2].id
output apimSubnetId     string = vnet.properties.subnets[3].id
output keyVaultSubnetId string = vnet.properties.subnets[4].id
output sqlDnsZoneId     string = dnsZones[0].id
output cosmosDnsZoneId  string = dnsZones[1].id
output redisDnsZoneId   string = dnsZones[2].id
output sbDnsZoneId      string = dnsZones[3].id
output kvDnsZoneId      string = dnsZones[4].id
