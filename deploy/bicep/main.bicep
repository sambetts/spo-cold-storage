// SPO Cold Storage — single Windows App Service deployment.
// Resources:
//   - Log Analytics + Application Insights (workspace-based)
//   - Windows App Service Plan + Web App (system-assigned MSI, AlwaysOn)
//   - Storage Account + blob container (CORS for SPA)
//   - Key Vault (RBAC mode)
//   - Service Bus namespace + queue (5 min lock, max delivery 1000)
//   - Azure SQL Server + Database (Entra-only auth)
//   - Azure AI Search
//   - Role assignments and Key Vault secrets

targetScope = 'resourceGroup'

@description('Azure region')
param location string = resourceGroup().location

@description('Resource tags')
param tags object = {}

@description('Naming object — see params.example.json')
param naming object

@description('SKU object — see params.example.json')
param sku object

@description('Azure AD configuration')
param azureAd object

@description('SQL Entra admin configuration')
param sql object

@description('SharePoint configuration')
param sharePoint object

@description('Public IP of the deploying machine — added to SQL firewall during deploy. Leave empty to skip.')
param deployClientIpAddress string = ''

@description('Object IDs of users / groups that should get Storage Blob Data Reader on the storage account. End-user browsers call blob storage directly with a user-scoped token, so the signed-in users (or a group containing them) need data-plane RBAC. Leave empty to skip.')
param storageUserDataReaderPrincipals array = []

@description('Optional: principalType for each entry in storageUserDataReaderPrincipals. Must be same length when supplied; values: User | Group | ServicePrincipal. Defaults to User when omitted.')
param storageUserDataReaderTypes array = []

// Secret values written to Key Vault. Empty strings are skipped so phases can be re-run.
@secure()
@description('Azure AD application client secret. Stored as Key Vault secret aad-client-secret.')
param aadClientSecret string = ''

// ---------- Log Analytics + App Insights ----------
resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: naming.logAnalytics
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
  }
}

resource appi 'Microsoft.Insights/components@2020-02-02' = {
  name: naming.appInsights
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: law.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ---------- Storage ----------
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: naming.storageAccount
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    accessTier: 'Hot'
    publicNetworkAccess: 'Enabled'
  }
}

resource blobSvc 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    cors: {
      corsRules: [
        {
          allowedOrigins: [ 'https://${naming.webApp}.azurewebsites.net' ]
          allowedMethods: [ 'GET', 'HEAD', 'OPTIONS' ]
          allowedHeaders: [ '*' ]
          exposedHeaders: [ '*' ]
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}

resource blobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobSvc
  name: naming.blobContainer
  properties: {
    publicAccess: 'None'
  }
}

// ---------- Key Vault (RBAC) ----------
resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: naming.keyVault
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 30
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

// ---------- Service Bus ----------
resource sb 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: naming.serviceBus
  location: location
  tags: tags
  sku: { name: sku.serviceBus, tier: sku.serviceBus }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource sbQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: sb
  name: naming.serviceBusQueue
  properties: {
    lockDuration: 'PT5M'
    maxDeliveryCount: 1000
    enablePartitioning: false
    requiresSession: false
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
  }
}

// ---------- SQL ----------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: naming.sqlServer
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: sql.entraAdminIsGroup ? 'Group' : 'User'
      login: sql.entraAdminLogin
      sid: sql.entraAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: naming.sqlDatabase
  location: location
  tags: tags
  sku: { name: sku.sqlDatabase, tier: sku.sqlDatabase == 'S0' || startsWith(sku.sqlDatabase, 'S') ? 'Standard' : 'Basic' }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    zoneRedundant: false
  }
}

resource sqlFwAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlFwDeploy 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (!empty(deployClientIpAddress)) {
  parent: sqlServer
  name: 'AllowDeployClientIp'
  properties: {
    startIpAddress: deployClientIpAddress
    endIpAddress: deployClientIpAddress
  }
}

// ---------- Search ----------
resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: naming.search
  location: location
  tags: tags
  sku: { name: sku.search }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    publicNetworkAccess: 'enabled'
    authOptions: { apiKeyOnly: {} }
  }
}

// ---------- App Service Plan (Windows) ----------
resource asp 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: naming.appServicePlan
  location: location
  tags: tags
  sku: { name: sku.appServicePlan }
  kind: 'app'
  properties: { reserved: false }
}

// ---------- Web App ----------
resource web 'Microsoft.Web/sites@2023-12-01' = {
  name: naming.webApp
  location: location
  tags: tags
  kind: 'app'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: asp.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      alwaysOn: true
      http20Enabled: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      use32BitWorkerProcess: false
      webSocketsEnabled: false
      netFrameworkVersion: 'v9.0'
    }
  }
}

// ---------- Diagnostic settings → Log Analytics ----------
resource webDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: web
  name: 'to-law'
  properties: {
    workspaceId: law.id
    logs: [
      { category: 'AppServiceHTTPLogs', enabled: true }
      { category: 'AppServiceConsoleLogs', enabled: true }
      { category: 'AppServiceAppLogs', enabled: true }
      { category: 'AppServicePlatformLogs', enabled: true }
    ]
    metrics: [ { category: 'AllMetrics', enabled: true } ]
  }
}

// ---------- Key Vault secrets (placeholders / values from params) ----------
// These let the Web App reference connection strings via @Microsoft.KeyVault(...).
// Phase Secrets in deploy.ps1 re-runs this with real values.

resource secretAadClient 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(aadClientSecret)) {
  parent: kv
  name: 'aad-client-secret'
  properties: { value: aadClientSecret, contentType: 'text/plain' }
}

resource secretStorageConn 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'storage-connection-string'
  properties: {
    value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
    contentType: 'text/plain'
  }
}

resource secretSbConn 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'servicebus-connection-string'
  properties: {
    value: listKeys('${sb.id}/AuthorizationRules/RootManageSharedAccessKey', sb.apiVersion).primaryConnectionString
    contentType: 'text/plain'
  }
}

resource secretSearchQueryKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'search-query-key'
  properties: {
    value: search.listQueryKeys().value[0].key
    contentType: 'text/plain'
  }
}

resource secretSearchAdminKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'search-admin-key'
  properties: {
    value: search.listAdminKeys().primaryKey
    contentType: 'text/plain'
  }
}

// ---------- Role assignments ----------
// Built-in role definition IDs
var roleIds = {
  KeyVaultSecretsUser:          '4633458b-17de-408a-b874-0445c86b69e6'
  KeyVaultCertificatesUser:     'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba'
  StorageBlobDataContributor:   'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  ServiceBusDataSender:         '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  ServiceBusDataReceiver:       '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  SearchIndexDataContributor:   '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
  SearchServiceContributor:     '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
}

// Web App MSI → Key Vault Secrets User (resolves @Microsoft.KeyVault references)
resource raWebKvSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: kv
  name: guid(kv.id, web.id, roleIds.KeyVaultSecretsUser)
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.KeyVaultSecretsUser)
  }
}

// AAD app registration SP → Key Vault Secrets User + Certificates User (needed by workers' cert flow)
resource raAadKvSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(azureAd.servicePrincipalObjectId)) {
  scope: kv
  name: guid(kv.id, azureAd.servicePrincipalObjectId, roleIds.KeyVaultSecretsUser)
  properties: {
    principalId: azureAd.servicePrincipalObjectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.KeyVaultSecretsUser)
  }
}

resource raAadKvCerts 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(azureAd.servicePrincipalObjectId)) {
  scope: kv
  name: guid(kv.id, azureAd.servicePrincipalObjectId, roleIds.KeyVaultCertificatesUser)
  properties: {
    principalId: azureAd.servicePrincipalObjectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.KeyVaultCertificatesUser)
  }
}

// Web App MSI → Storage Blob Data Contributor
resource raWebStorage 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, web.id, roleIds.StorageBlobDataContributor)
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.StorageBlobDataContributor)
  }
}

// End-user / group → Storage Blob Data Reader on the storage account.
// Needed because the SPA calls Azure Blob Storage directly with a user-scoped token
// (https://storage.azure.com/user_impersonation), bypassing the Web App MSI.
var storageReaderId = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'  // Storage Blob Data Reader

resource raStorageUserReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (oid, i) in storageUserDataReaderPrincipals: {
  scope: storage
  name: guid(storage.id, oid, storageReaderId)
  properties: {
    principalId: oid
    principalType: length(storageUserDataReaderTypes) > i ? storageUserDataReaderTypes[i] : 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageReaderId)
  }
}]

// Web App MSI → Service Bus Sender + Receiver
resource raWebSbSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: sb
  name: guid(sb.id, web.id, roleIds.ServiceBusDataSender)
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.ServiceBusDataSender)
  }
}

resource raWebSbReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: sb
  name: guid(sb.id, web.id, roleIds.ServiceBusDataReceiver)
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.ServiceBusDataReceiver)
  }
}

// Web App MSI → Search Index Data Contributor + Search Service Contributor
resource raWebSearchIndex 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: search
  name: guid(search.id, web.id, roleIds.SearchIndexDataContributor)
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.SearchIndexDataContributor)
  }
}

resource raWebSearchService 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: search
  name: guid(search.id, web.id, roleIds.SearchServiceContributor)
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.SearchServiceContributor)
  }
}

// ---------- Outputs ----------
output webAppName string                = web.name
output webAppHostname string            = web.properties.defaultHostName
output webAppPrincipalId string         = web.identity.principalId
output keyVaultName string              = kv.name
output keyVaultUri string               = kv.properties.vaultUri
output storageAccountName string        = storage.name
output blobContainerName string         = blobContainer.name
output serviceBusNamespace string       = sb.name
output serviceBusFqdn string            = '${sb.name}.servicebus.windows.net'
output serviceBusQueueName string       = sbQueue.name
output sqlServerName string             = sqlServer.name
output sqlServerFqdn string             = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string           = sqlDb.name
output searchServiceName string         = search.name
output searchServiceEndpoint string     = 'https://${search.name}.search.windows.net'
output appInsightsConnectionString string = appi.properties.ConnectionString
output baseServerAddress string         = sharePoint.baseServerAddress
output aadClientId string               = azureAd.clientId
output aadTenantId string               = azureAd.tenantId
output aadCertificateName string        = azureAd.certificateName
