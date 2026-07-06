// ════════════════════════════════════════════════════════════════════════════
// Cloud-Native Logistics Platform — Azure Infrastructure
// main.bicep  (entry point — deploys all modules)
//
// Deploy:
//   az deployment group create \
//     --resource-group rg-logistics-prod \
//     --template-file main.bicep \
//     --parameters @parameters/prod.bicepparam
// ════════════════════════════════════════════════════════════════════════════

targetScope = 'resourceGroup'

// ── Parameters ───────────────────────────────────────────────────────────────
@description('Environment name — drives all resource naming.')
@allowed(['dev', 'staging', 'prod'])
param environment string

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Short location suffix used in resource names (e.g. "eus" for East US).')
param locationSuffix string

@description('Object ID of the CI/CD service principal granted Key Vault access for deployments.')
param ciCdPrincipalObjectId string

@description('Enable zone-redundant deployments for SQL and Redis (prod only).')
param enableZoneRedundancy bool = (environment == 'prod')

// ── Naming convention ─────────────────────────────────────────────────────────
// All resources follow: {type}-logistics-{env}-{locationSuffix}
var prefix = 'logistics-${environment}-${locationSuffix}'

// ── Tags applied to every resource ───────────────────────────────────────────
var commonTags = {
  environment: environment
  project:     'logistics-platform'
  managedBy:   'bicep'
  costCentre:  'logistics-engineering'
}

// ════════════════════════════════════════════════════════════════════════════
// MODULE DEPLOYMENTS — ordered by dependency
// ════════════════════════════════════════════════════════════════════════════

// 1. Networking — VNet, subnets, NSGs, Private DNS zones
module networking 'modules/networking.bicep' = {
  name: 'networking'
  params: {
    prefix:      prefix
    location:    location
    tags:        commonTags
  }
}

// 2. Security — Key Vault, Managed Identities, RBAC role assignments
module security 'modules/security.bicep' = {
  name: 'security'
  params: {
    prefix:                  prefix
    location:                location
    tags:                    commonTags
    vnetId:                  networking.outputs.vnetId
    keyVaultSubnetId:        networking.outputs.keyVaultSubnetId
    ciCdPrincipalObjectId:   ciCdPrincipalObjectId
  }
}

// 3. Monitoring — Log Analytics, Application Insights
module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    prefix:   prefix
    location: location
    tags:     commonTags
  }
}

// 4. Databases — Azure SQL, Cosmos DB, Redis
module databases 'modules/databases.bicep' = {
  name: 'databases'
  params: {
    prefix:               prefix
    location:             location
    tags:                 commonTags
    enableZoneRedundancy: enableZoneRedundancy
    vnetId:               networking.outputs.vnetId
    dbSubnetId:           networking.outputs.dbSubnetId
    keyVaultName:         security.outputs.keyVaultName
    orderApiIdentityId:   security.outputs.orderApiIdentityPrincipalId
    workerIdentityId:     security.outputs.workerIdentityPrincipalId
    inventoryIdentityId:  security.outputs.inventoryIdentityPrincipalId
  }
}

// 5. Messaging — Service Bus (Premium, FIFO queue)
module messaging 'modules/messaging.bicep' = {
  name: 'messaging'
  params: {
    prefix:             prefix
    location:           location
    tags:               commonTags
    vnetId:             networking.outputs.vnetId
    messagingSubnetId:  networking.outputs.messagingSubnetId
    keyVaultName:       security.outputs.keyVaultName
    orderApiIdentityId: security.outputs.orderApiIdentityPrincipalId
    workerIdentityId:   security.outputs.workerIdentityPrincipalId
    webAppIdentityId:   security.outputs.webAppIdentityPrincipalId
  }
}

// 6. Compute — Container Apps, Azure Functions, APIM, Front Door
module compute 'modules/compute.bicep' = {
  name: 'compute'
  params: {
    prefix:                   prefix
    location:                 location
    tags:                     commonTags
    environment:              environment
    vnetId:                   networking.outputs.vnetId
    computeSubnetId:          networking.outputs.computeSubnetId
    apimSubnetId:             networking.outputs.apimSubnetId
    logAnalyticsWorkspaceId:  monitoring.outputs.logAnalyticsWorkspaceId
    appInsightsConnString:    monitoring.outputs.appInsightsConnectionString
    keyVaultUri:              security.outputs.keyVaultUri
    orderServiceBusNamespace: messaging.outputs.serviceBusNamespace
    orderQueueName:           messaging.outputs.orderQueueName
    webAppIdentityId:         security.outputs.webAppIdentityId
    orderApiIdentityId:       security.outputs.orderApiIdentityId
    workerIdentityId:         security.outputs.workerIdentityId
    inventoryIdentityId:      security.outputs.inventoryIdentityId
  }
}

// ── Outputs (consumed by CI/CD pipelines) ────────────────────────────────────
output keyVaultUri             string = security.outputs.keyVaultUri
output containerAppsEnvName   string = compute.outputs.containerAppsEnvName
output orderServiceFqdn        string = compute.outputs.orderServiceFqdn
output inventoryServiceFqdn    string = compute.outputs.inventoryServiceFqdn
output webAppFqdn              string = compute.outputs.webAppFqdn
output apimGatewayUrl          string = compute.outputs.apimGatewayUrl
