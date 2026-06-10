targetScope = 'resourceGroup'

@description('Resource prefix used for alert names.')
param namePrefix string

@description('Azure region for alert resources.')
param location string

@description('Application Insights resource id queried by the alert rules.')
param applicationInsightsId string

@description('Operations email that receives production alerts.')
param operationsEmail string

@description('Resource tags.')
param tags object = {}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${namePrefix}-operations'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: take(replace(namePrefix, '-', ''), 12)
    enabled: true
    emailReceivers: [
      {
        name: 'operations-email'
        emailAddress: operationsEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

resource bookingFailures 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-booking-failures'
  location: location
  tags: tags
  properties: {
    displayName: 'M1 booking failures'
    description: 'Booking provider or booking workflow failures were detected.'
    enabled: true
    severity: 1
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [
      applicationInsightsId
    ]
    criteria: {
      allOf: [
        {
          query: 'customEvents | where name in ("booking.failed", "workflow.failed")'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}

resource confirmationFailures 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-confirmation-failures'
  location: location
  tags: tags
  properties: {
    displayName: 'M1 confirmation retry failures'
    description: 'A booking confirmation could not be queued or a queued retry failed.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [
      applicationInsightsId
    ]
    criteria: {
      allOf: [
        {
          query: 'customEvents | where name in ("confirmation.retry.schedule_failed", "confirmation.retry.failed")'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}

resource webhookSecurityFailures 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-webhook-security'
  location: location
  tags: tags
  properties: {
    displayName: 'M1 webhook authentication failures'
    description: 'Repeated invalid Vapi or Twilio webhook authentication attempts were detected.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [
      applicationInsightsId
    ]
    criteria: {
      allOf: [
        {
          query: 'customEvents | where name == "security.auth_failed"'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 4
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}

output actionGroupId string = actionGroup.id
