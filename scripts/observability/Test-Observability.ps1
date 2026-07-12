param(
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$elasticsearch = 'http://127.0.0.1:9209'
$kibana = 'http://127.0.0.1:5609'
$kibanaHeaders = @{ 'kbn-xsrf' = 'true' }
$dashboardPath = Join-Path $root 'deploy\observability\astra-dashboard.json'
$poolRulePath = Join-Path $root 'deploy\observability\postgres-pool-timeout-rule.json'
$deadLetterRulePath = Join-Path $root 'deploy\observability\outbox-dead-letter-rule.json'
$probeProject = Join-Path $root 'tests\Astra.ObservabilityProbe\Astra.ObservabilityProbe.csproj'
$poolRuleId = 'astra-postgres-pool-timeout'
$deadLetterRuleId = 'astra-outbox-dead-letter'

function Invoke-JsonRequest {
    param(
        [ValidateSet('Get', 'Post', 'Put', 'Delete')]
        [string]$Method,
        [string]$Uri,
        [string]$Body,
        [hashtable]$Headers = @{}
    )

    $parameters = @{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
        ContentType = 'application/json'
        TimeoutSec = 15
    }
    if ($Body) {
        $parameters.Body = $Body
    }

    Invoke-RestMethod @parameters
}

function Get-EsqlCount {
    param([string]$Query)

    try {
        $body = @{ query = $Query } | ConvertTo-Json -Compress
        $result = Invoke-JsonRequest -Method Post -Uri "$elasticsearch/_query" -Body $body
        if ($result.values.Count -eq 0) {
            return 0
        }

        return [long]$result.values[0][0]
    }
    catch {
        return 0
    }
}

function Wait-ForCount {
    param(
        [string]$Query,
        [int]$Timeout
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Timeout)
    do {
        $count = Get-EsqlCount -Query $Query
        if ($count -gt 0) {
            return $count
        }

        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Telemetry query returned no documents within ${Timeout}s: $Query"
}

function Reset-KibanaRule {
    param(
        [string]$RuleId,
        [string]$DefinitionPath
    )

    try {
        Invoke-WebRequest `
            -UseBasicParsing `
            -Method Delete `
            -Uri "$kibana/api/alerting/rule/$RuleId" `
            -Headers $kibanaHeaders `
            -TimeoutSec 15 | Out-Null
    }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) {
            throw
        }
    }

    Invoke-JsonRequest `
        -Method Post `
        -Uri "$kibana/api/alerting/rule/$RuleId" `
        -Body (Get-Content $DefinitionPath -Raw) `
        -Headers $kibanaHeaders
}

Invoke-RestMethod -Uri "$elasticsearch/_cluster/health" -TimeoutSec 10 | Out-Null
Invoke-RestMethod -Uri "$kibana/api/status" -TimeoutSec 10 | Out-Null
Invoke-RestMethod -Uri 'http://127.0.0.1:13133/' -TimeoutSec 10 | Out-Null

$clusterSettings = @{
    persistent = @{
        'data_streams.lifecycle.retention.default' = '1h'
        'data_streams.lifecycle.retention.max' = '2h'
        'data_streams.lifecycle.poll_interval' = '1m'
        'cluster.lifecycle.default.rollover' = 'max_age=15m,max_primary_shard_size=64mb,min_docs=1'
    }
} | ConvertTo-Json -Depth 4 -Compress
Invoke-JsonRequest -Method Put -Uri "$elasticsearch/_cluster/settings" -Body $clusterSettings | Out-Null

$env:ASTRA_OTLP_ENDPOINT = 'http://127.0.0.1:4317'
dotnet run --project $probeProject -c Release --no-launch-profile
if ($LASTEXITCODE -ne 0) {
    throw 'The initial OTLP probe failed.'
}

$traceQuery = 'FROM traces-generic.otel-* | WHERE service.name == "Astra.ObservabilityProbe" | STATS count = COUNT()'
$metricQuery = 'FROM metrics-generic.otel-* | WHERE `metrics.astra.observability.probe` IS NOT NULL | STATS count = COUNT()'
$traceCount = Wait-ForCount -Query $traceQuery -Timeout $TimeoutSeconds
$metricCount = Wait-ForCount -Query $metricQuery -Timeout $TimeoutSeconds

$streams = Invoke-RestMethod -Uri "$elasticsearch/_data_stream" -TimeoutSec 15
$retentionBody = @{ data_retention = '1h'; enabled = $true } | ConvertTo-Json -Compress
foreach ($stream in $streams.data_streams) {
    if ($stream.name -match '^(logs|metrics|traces)-') {
        Invoke-JsonRequest `
            -Method Put `
            -Uri "$elasticsearch/_data_stream/$($stream.name)/_lifecycle" `
            -Body $retentionBody | Out-Null
    }
}

$poolRule = Reset-KibanaRule -RuleId $poolRuleId -DefinitionPath $poolRulePath
$deadLetterRule = Reset-KibanaRule -RuleId $deadLetterRuleId -DefinitionPath $deadLetterRulePath
$dashboardJson = Get-Content $dashboardPath -Raw
$dashboardTitle = ($dashboardJson | ConvertFrom-Json).title
$existingDashboards = Invoke-RestMethod `
    -Uri "$kibana/api/dashboards" `
    -Headers $kibanaHeaders `
    -TimeoutSec 15
foreach ($existing in $existingDashboards.dashboards) {
    if ($existing.data.title -eq $dashboardTitle) {
        Invoke-WebRequest `
            -UseBasicParsing `
            -Method Delete `
            -Uri "$kibana/api/dashboards/$($existing.id)" `
            -Headers $kibanaHeaders `
            -TimeoutSec 15 | Out-Null
    }
}

$dashboard = Invoke-JsonRequest `
    -Method Post `
    -Uri "$kibana/api/dashboards" `
    -Body $dashboardJson `
    -Headers $kibanaHeaders

dotnet run --project $probeProject -c Release --no-launch-profile -- --simulate-pool-timeout
if ($LASTEXITCODE -ne 0) {
    throw 'The synthetic pool-timeout probe failed.'
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$activeAlerts = 0
do {
    Start-Sleep -Seconds 5
    $poolRule = Invoke-RestMethod -Uri "$kibana/api/alerting/rule/$poolRuleId" -TimeoutSec 15
    $activeAlerts = [int]($poolRule.last_run.alerts_count.active ?? 0)
} while ($activeAlerts -eq 0 -and [DateTimeOffset]::UtcNow -lt $deadline)

if ($activeAlerts -eq 0) {
    throw "Kibana rule '$poolRuleId' did not become active within ${TimeoutSeconds}s."
}

$dashboardId = $dashboard.id
if (-not $dashboardId) {
    throw 'Kibana did not return a dashboard ID.'
}

Invoke-RestMethod -Uri "$kibana/api/dashboards/$dashboardId" -TimeoutSec 15 | Out-Null
$timeoutMetricCount = Wait-ForCount `
    -Query 'FROM metrics-generic.otel-* | EVAL failure_count = TO_LONG(`metrics.astra.postgres.connection.acquire.failures`) | WHERE failure_count > 0 AND attributes.reason == "timeout" | STATS count = COUNT()' `
    -Timeout $TimeoutSeconds

[pscustomobject]@{
    TraceDocuments = $traceCount
    MetricDocuments = $metricCount
    TimeoutMetricDocuments = $timeoutMetricCount
    ActiveAlerts = $activeAlerts
    PoolRuleStatus = $poolRule.execution_status.status
    DeadLetterRuleStatus = $deadLetterRule.execution_status.status
    Dashboard = "$kibana/app/dashboards#/view/$dashboardId"
}
