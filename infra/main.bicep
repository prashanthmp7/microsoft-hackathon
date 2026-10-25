// Second Half Mind: Azure deployment with least-privilege, keyless access.
//
// One user-assigned managed identity runs the app. It can:
//   - pull its image (AcrPull)
//   - read one secret (Key Vault Secrets User)
//   - SEND to Event Hubs only (Data Sender; no receive, no manage)
//   - call the model, Content Safety and Translator (Cognitive Services OpenAI User / Cognitive Services User)
// Local (key) auth is disabled on Event Hubs and the AI resources, so no keys exist to leak.
//
// Validate before deploying:  az deployment group what-if -g <rg> -f infra/main.bicep -p operatorKey=<secret>

targetScope = 'resourceGroup'

@description('Short name used as a prefix for resources.')
@minLength(3)
@maxLength(12)
param name string = 'shmind'

param location string = resourceGroup().location

@description('Container image to run. The deploy workflow replaces this after pushing to ACR.')
param containerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Operator key for match control endpoints. Stored only in Key Vault.')
@secure()
param operatorKey string

@description('Chat model deployed in the Foundry (AI Services) resource.')
param chatModelName string = 'gpt-4o-mini'
param chatModelVersion string = '2024-07-18'
param chatModelCapacity int = 20

var suffix = uniqueString(resourceGroup().id)

@description('Image per agent service, e.g. {registry}/shm-agent-stats:tag. The deploy workflow replaces these.')
param agentImages object = {
  vision: 'mcr.microsoft.com/k8se/quickstart:latest'
  stats: 'mcr.microsoft.com/k8se/quickstart:latest'
  explainer: 'mcr.microsoft.com/k8se/quickstart:latest'
  referee: 'mcr.microsoft.com/k8se/quickstart:latest'
  narrator: 'mcr.microsoft.com/k8se/quickstart:latest'
  producer: 'mcr.microsoft.com/k8se/quickstart:latest'
}

var agentNames = ['vision', 'stats', 'explainer', 'referee', 'narrator', 'producer']
var roles = {
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  eventHubsDataSender: '2b629674-e913-4c01-ae53-ef4638d8f975'
  cognitiveServicesOpenAIUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
  cognitiveServicesUser: 'a97b65f3-24c7-4388-baec-2e87135dc908'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-app-id'
  location: location
}

resource logs 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${name}-logs-${suffix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: '${name}acr${suffix}'
  location: location
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${name}-kv-${take(suffix, 8)}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
  }
}

resource operatorSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'operator-key'
  properties: { value: operatorKey }
}

resource eventHubs 'Microsoft.EventHub/namespaces@2024-01-01' = {
  name: '${name}-events-${suffix}'
  location: location
  sku: { name: 'Standard', tier: 'Standard', capacity: 1 }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

resource matchEventsHub 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = {
  parent: eventHubs
  name: 'match-events'
  properties: {
    partitionCount: 2
    messageRetentionInDays: 1
  }
}

// Fabric Eventstream reads from this consumer group.
resource fabricConsumerGroup 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2024-01-01' = {
  parent: matchEventsHub
  name: 'fabric'
}

resource foundry 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: '${name}-foundry-${suffix}'
  location: location
  kind: 'AIServices'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: '${name}-foundry-${suffix}'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: foundry
  name: chatModelName
  sku: { name: 'GlobalStandard', capacity: chatModelCapacity }
  properties: {
    model: { format: 'OpenAI', name: chatModelName, version: chatModelVersion }
    raiPolicyName: 'Microsoft.DefaultV2'
  }
}

resource translator 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: '${name}-translator-${suffix}'
  location: location
  kind: 'TextTranslation'
  sku: { name: 'S1' }
  properties: {
    customSubDomainName: '${name}-translator-${suffix}'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

// ---- Least-privilege role assignments for the app identity ----

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, identity.id, roles.acrPull)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
  }
}

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: operatorSecret
  name: guid(operatorSecret.id, identity.id, roles.keyVaultSecretsUser)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
  }
}

resource eventSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: matchEventsHub
  name: guid(matchEventsHub.id, identity.id, roles.eventHubsDataSender)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.eventHubsDataSender)
  }
}

resource openAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: foundry
  name: guid(foundry.id, identity.id, roles.cognitiveServicesOpenAIUser)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesOpenAIUser)
  }
}

resource contentSafetyUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: foundry
  name: guid(foundry.id, identity.id, roles.cognitiveServicesUser)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesUser)
  }
}

resource translatorUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: translator
  name: guid(translator.id, identity.id, roles.cognitiveServicesUser)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesUser)
  }
}

// ---- Hosting ----

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${name}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${name}-app'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [acrPull, secretsUser, platformSendsRequests, platformReadsResults]
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        // SignalR needs sticky sessions if this is ever scaled beyond one replica.
        stickySessions: { affinity: 'sticky' }
      }
      registries: [
        { server: registry.properties.loginServer, identity: identity.id }
      ]
      secrets: [
        { name: 'operator-key', keyVaultUrl: '${vault.properties.vaultUri}secrets/operator-key', identity: identity.id }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'Security__OperatorKey', secretRef: 'operator-key' }
            { name: 'AI__UseManagedIdentity', value: 'true' }
            { name: 'AI__ManagedIdentityClientId', value: identity.properties.clientId }
            { name: 'AI__FoundryEndpoint', value: 'https://${foundry.properties.customSubDomainName}.openai.azure.com' }
            { name: 'AI__ChatDeployment', value: chatDeployment.name }
            { name: 'AI__ContentSafetyEndpoint', value: 'https://${foundry.properties.customSubDomainName}.cognitiveservices.azure.com' }
            { name: 'AI__TranslatorEndpoint', value: 'https://${translator.properties.customSubDomainName}.cognitiveservices.azure.com/translator/text/v3.0' }
            { name: 'AI__TranslatorRegion', value: location }
            { name: 'AI__TranslatorResourceId', value: translator.id }
            { name: 'EventHubs__Namespace', value: '${eventHubs.name}.servicebus.windows.net' }
            { name: 'EventHubs__HubName', value: matchEventsHub.name }
            { name: 'Agents__Mode', value: 'ServiceBus' }
            { name: 'Agents__ServiceBusNamespace', value: '${agentBus.name}.servicebus.windows.net' }
            { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
          ]
          probes: [
            { type: 'Liveness', httpGet: { path: '/api/health', port: 8080 } }
          ]
        }
      ]
      // State is in memory in this reference build, so run exactly one replica.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

// ---- Agent services: one container app, identity and queue per agent ----
// Service Bus has local (key) auth disabled. RBAC is scoped per queue:
//   platform  -> send to every agent queue, receive from the results queue
//   agent X   -> receive from agent-X only, send to the results queue only

resource agentBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: '${name}-agents-${suffix}'
  location: location
  sku: { name: 'Standard', tier: 'Standard' }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

resource agentQueues 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = [for agent in agentNames: {
  parent: agentBus
  name: 'agent-${agent}'
  properties: {
    defaultMessageTimeToLive: 'PT1M'
    maxDeliveryCount: 3
    deadLetteringOnMessageExpiration: false
  }
}]

resource resultsQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: agentBus
  name: 'agent-results'
  properties: {
    defaultMessageTimeToLive: 'PT1M'
    maxDeliveryCount: 3
  }
}

resource agentIdentities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [for agent in agentNames: {
  name: '${name}-agent-${agent}-id'
  location: location
}]

resource platformSendsRequests 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (agent, i) in agentNames: {
  scope: agentQueues[i]
  name: guid(agentQueues[i].id, identity.id, roles.serviceBusDataSender)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
  }
}]

resource platformReadsResults 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: resultsQueue
  name: guid(resultsQueue.id, identity.id, roles.serviceBusDataReceiver)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
  }
}

resource agentReadsOwnQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (agent, i) in agentNames: {
  scope: agentQueues[i]
  name: guid(agentQueues[i].id, agentIdentities[i].id, roles.serviceBusDataReceiver)
  properties: {
    principalId: agentIdentities[i].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
  }
}]

resource agentSendsResults 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (agent, i) in agentNames: {
  scope: resultsQueue
  name: guid(resultsQueue.id, agentIdentities[i].id, roles.serviceBusDataSender)
  properties: {
    principalId: agentIdentities[i].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
  }
}]

resource agentAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (agent, i) in agentNames: {
  scope: registry
  name: guid(registry.id, agentIdentities[i].id, roles.acrPull)
  properties: {
    principalId: agentIdentities[i].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
  }
}]

// No ingress: agent services are reachable only through their queue.
resource agentApps 'Microsoft.App/containerApps@2024-03-01' = [for (agent, i) in agentNames: {
  name: '${name}-agent-${agent}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${agentIdentities[i].id}': {} }
  }
  dependsOn: [agentAcrPull, agentReadsOwnQueue, agentSendsResults]
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      registries: [
        { server: registry.properties.loginServer, identity: agentIdentities[i].id }
      ]
    }
    template: {
      containers: [
        {
          name: agent
          image: agentImages[agent]
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
            { name: 'Agents__ServiceBusNamespace', value: '${agentBus.name}.servicebus.windows.net' }
            { name: 'AZURE_CLIENT_ID', value: agentIdentities[i].properties.clientId }
          ]
        }
      ]
      // Agents keep per-match memory in process and must see events in order: one replica.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}]

output appUrl string = 'https://${app.properties.configuration.ingress.fqdn}'
output registryLoginServer string = registry.properties.loginServer
output eventHubsNamespace string = '${eventHubs.name}.servicebus.windows.net'
output appIdentityPrincipalId string = identity.properties.principalId
