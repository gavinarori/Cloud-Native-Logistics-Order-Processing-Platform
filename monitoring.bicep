// ════════════════════════════════════════════════════════════════════════════
// monitoring.bicep — Log Analytics + Application Insights
// ════════════════════════════════════════════════════════════════════════════

param prefix   string
param location string
param tags     object

// ── Log Analytics Workspace ───────────────────────────────────────────────────
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name:     'log-${prefix}'
  location: location
  tags:     tags
  properties: {
    sku:                    { name: 'PerGB2018' }
    retentionInDays:        90
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery:     'Enabled'
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

// ── Application Insights ──────────────────────────────────────────────────────
// Workspace-based (modern) — logs flow into the Log Analytics workspace above.
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name:     'appi-${prefix}'
  location: location
  tags:     tags
  kind:     'web'
  properties: {
    Application_Type:                'web'
    WorkspaceResourceId:             logAnalytics.id
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery:     'Enabled'
    RetentionInDays:                 90
    SamplingPercentage:              20  // Reduce cost in prod; raise for debugging
  }
}

// ── Alert: high dead-letter count ─────────────────────────────────────────────
// Fires when the DeadLetterProcessor emits "OrderDeadLettered" events at volume.
resource deadLetterAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name:     'alert-dead-letters-${prefix}'
  location: location
  tags:     tags
  properties: {
    displayName:  'High Dead-Letter Rate'
    description:  'Fires when dead-lettered orders exceed 5 in a 5-minute window.'
    severity:     1  // Critical
    enabled:      true
    evaluationFrequency: 'PT5M'
    windowSize:          'PT5M'
    scopes: [logAnalytics.id]
    criteria: {
      allOf: [{
        query: '''
          traces
          | where message contains "OrderDeadLettered"
          | summarize count() by bin(timestamp, 5m)
          | where count_ > 5
        '''
        timeAggregation:    'Count'
        operator:           'GreaterThan'
        threshold:          0
        failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
      }]
    }
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────
output logAnalyticsWorkspaceId    string = logAnalytics.id
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output appInsightsInstrumentationKey string = appInsights.properties.InstrumentationKey
