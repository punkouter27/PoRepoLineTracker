// Availability (ping) test for the web app's /health endpoint.
// NOTE: A web test MUST live in the SAME resource group AND region as the App Insights
// component it links to — hence this is deployed as a module scoped to PoShared/eastus2.

@description('Resource ID of the App Insights component to link the test to')
param appInsightsId string

@description('Location — MUST match the linked App Insights component location')
param location string

@description('Public host name of the web app to ping (no scheme)')
param webAppHostName string

@description('Web app name, used to name the test')
param webAppName string

resource healthAvailabilityTest 'Microsoft.Insights/webtests@2022-06-15' = {
  name: 'avail-${webAppName}-health'
  location: location
  tags: {
    'hidden-link:${appInsightsId}': 'Resource'
  }
  kind: 'ping'
  properties: {
    SyntheticMonitorId: 'avail-${webAppName}-health'
    Name: '${webAppName}-health'
    Enabled: true
    Frequency: 300
    Timeout: 30
    Kind: 'ping'
    RetryEnabled: true
    Locations: [
      { Id: 'us-ca-sjc-azr' }
      { Id: 'us-tx-sn1-azr' }
      { Id: 'us-il-ch1-azr' }
    ]
    Configuration: {
      WebTest: '<WebTest Name="${webAppName}-health" Enabled="True" CssProjectStructure="" CssIteration="" Timeout="30" WorkItemIds="" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" Description="" CredentialUserName="" CredentialPassword="" PreAuthenticate="True" Proxy="default" StopOnError="False" RecordedResultFile="" ResultsLocale=""><Items><Request Method="GET" Version="1.1" Url="https://${webAppHostName}/health" ThinkTime="0" Timeout="30" ParseDependentRequests="False" FollowRedirects="True" RecordResult="True" Cache="False" ResponseTimeGoal="0" Encoding="utf-8" ExpectedHttpStatusCode="200" ExpectedResponseUrl="" ReportingName="" IgnoreHttpStatusCode="False" /></Items></WebTest>'
    }
  }
}
