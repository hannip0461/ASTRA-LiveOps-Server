param(
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$elasticsearch = 'http://127.0.0.1:9209'
$kibana = 'http://127.0.0.1:5609'
$probeProject = Join-Path $root 'tests\Astra.ObservabilityProbe\Astra.ObservabilityProbe.csproj'
$evidenceDirectory = Join-Path $root 'output\evidence'
$evidencePath = Join-Path $evidenceDirectory 'operational-scenarios.json'
$deadLetterRuleId = 'astra-outbox-dead-letter'

function Invoke-Esql {
    param([string]$Query)

    $body = @{ query = $Query } | ConvertTo-Json -Compress
    Invoke-RestMethod `
        -Method Post `
        -Uri "$elasticsearch/_query?format=json" `
        -ContentType 'application/json' `
        -Body $body `
        -TimeoutSec 15
}

function Wait-EsqlRow {
    param(
        [string]$Query,
        [scriptblock]$Ready,
        [int]$Timeout
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Timeout)
    do {
        try {
            $result = Invoke-Esql -Query $Query
            if ($result.values.Count -gt 0 -and (& $Ready $result.values[0])) {
                return ,$result.values[0]
            }
        }
        catch {
            # A metric field may not be mapped until its first export.
        }

        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Operational telemetry did not satisfy the expected condition within ${Timeout}s: $Query"
}

Invoke-RestMethod -Uri 'http://127.0.0.1:5191/health' -TimeoutSec 10 | Out-Null
& (Join-Path $PSScriptRoot 'Test-Observability.ps1') -TimeoutSeconds $TimeoutSeconds | Out-Host

$scenarioStartedAt = [DateTimeOffset]::UtcNow
$env:ASTRA_OTLP_ENDPOINT = 'http://127.0.0.1:4317'
$env:ASTRA_POSTGRES_CONNECTION = 'Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password'
$probeOutput = @(
    dotnet run `
        --project $probeProject `
        -c Release `
        --no-launch-profile `
        --no-build `
        --no-restore `
        -- `
        --saturate-postgres `
        --exercise-outbox
)
if ($LASTEXITCODE -ne 0) {
    throw "The operational probe failed: $($probeOutput -join [Environment]::NewLine)"
}

$probeJson = $probeOutput | Where-Object { $_.TrimStart().StartsWith('{') } | Select-Object -Last 1
if (-not $probeJson) {
    throw 'The operational probe returned no JSON result.'
}

$probe = $probeJson | ConvertFrom-Json
$poolQuery = @'
FROM metrics-generic.otel-*
| WHERE `resource.attributes.service.instance.id` == "__PROBE_ID__"
| EVAL attempts = COALESCE(TO_LONG(`metrics.astra.postgres.connection.acquire.attempts`), 0), failures = COALESCE(TO_LONG(`metrics.astra.postgres.connection.acquire.failures`), 0)
| STATS attempts = SUM(attempts), failures = SUM(failures)
| EVAL failure_rate = TO_DOUBLE(failures) / TO_DOUBLE(attempts), burn_rate = failure_rate / 0.001
'@.Replace('__PROBE_ID__', $probe.probeId)
$poolRow = Wait-EsqlRow `
    -Query $poolQuery `
    -Ready { param($row) [long]$row[0] -ge 4 -and [long]$row[1] -ge 1 } `
    -Timeout $TimeoutSeconds

$startedAt = $scenarioStartedAt.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$outboxQuery = @'
FROM metrics-generic.otel-*
| WHERE @timestamp >= TO_DATETIME("__STARTED_AT__") AND `resource.attributes.service.name` == "Astra.Worker"
| EVAL published = COALESCE(TO_LONG(`metrics.astra.outbox.published`), 0), retries = COALESCE(TO_LONG(`metrics.astra.outbox.retry_scheduled`), 0), dead_letters = COALESCE(TO_LONG(`metrics.astra.outbox.dead_lettered`), 0)
| STATS published = SUM(published), retries = SUM(retries), dead_letters = SUM(dead_letters)
'@.Replace('__STARTED_AT__', $startedAt)
$outboxRow = Wait-EsqlRow `
    -Query $outboxQuery `
    -Ready { param($row) [long]$row[0] -ge 1 -and [long]$row[1] -ge 1 -and [long]$row[2] -ge 1 } `
    -Timeout $TimeoutSeconds

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$activeDeadLetterAlerts = 0
do {
    $deadLetterRule = Invoke-RestMethod `
        -Uri "$kibana/api/alerting/rule/$deadLetterRuleId" `
        -TimeoutSec 15
    $activeDeadLetterAlerts = [int]($deadLetterRule.last_run.alerts_count.active ?? 0)
    if ($activeDeadLetterAlerts -gt 0) {
        break
    }

    Start-Sleep -Seconds 5
} while ([DateTimeOffset]::UtcNow -lt $deadline)

if ($activeDeadLetterAlerts -eq 0) {
    throw "Kibana rule '$deadLetterRuleId' did not become active within ${TimeoutSeconds}s."
}

$dashboards = Invoke-RestMethod `
    -Uri "$kibana/api/dashboards" `
    -Headers @{ 'kbn-xsrf' = 'true' } `
    -TimeoutSec 15
$dashboard = $dashboards.dashboards |
    Where-Object { $_.data.title -eq 'ASTRA LiveOps - Runtime and PostgreSQL' } |
    Select-Object -First 1
if (-not $dashboard) {
    throw 'The ASTRA observability dashboard was not found.'
}

$evidence = [ordered]@{
    capturedAtUtc = [DateTimeOffset]::UtcNow
    probeId = $probe.probeId
    pool = [ordered]@{
        configuredSize = $probe.poolSaturation.PoolSize
        timeoutMilliseconds = $probe.poolSaturation.TimeoutMilliseconds
        recoveryProbe = $probe.poolSaturation.RecoveryProbe
        observedAttempts = [long]$poolRow[0]
        observedTimeouts = [long]$poolRow[1]
        failureRate = [double]$poolRow[2]
        burnRate = [double]$poolRow[3]
    }
    outbox = [ordered]@{
        publishedEventId = $probe.outboxDelivery.PublishedEventId
        publishedStatus = $probe.outboxDelivery.PublishedStatus
        deadLetterEventId = $probe.outboxDelivery.DeadLetterEventId
        deadLetterStatus = $probe.outboxDelivery.DeadLetterStatus
        attempts = $probe.outboxDelivery.Attempts
        errorCode = $probe.outboxDelivery.ErrorCode
        observedPublished = [long]$outboxRow[0]
        observedRetries = [long]$outboxRow[1]
        observedDeadLetters = [long]$outboxRow[2]
    }
    alerts = [ordered]@{
        outboxDeadLetterActive = $activeDeadLetterAlerts
    }
    dashboard = "$kibana/app/dashboards#/view/$($dashboard.id)"
}

New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null
[System.IO.File]::WriteAllText(
    $evidencePath,
    ($evidence | ConvertTo-Json -Depth 8),
    [System.Text.UTF8Encoding]::new($false))
$evidence | ConvertTo-Json -Depth 8
