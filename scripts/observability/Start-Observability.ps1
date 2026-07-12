param(
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$composeFile = Join-Path $root 'deploy\docker-compose.yml'

function Wait-HttpEndpoint {
    param(
        [string]$Uri,
        [int]$Timeout
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Timeout)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                return
            }
        }
        catch {
            Start-Sleep -Seconds 2
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Endpoint did not become ready within ${Timeout}s: $Uri"
}

docker info | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker Desktop is not ready.'
}

docker compose -f $composeFile --profile observability config --quiet
if ($LASTEXITCODE -ne 0) {
    throw 'Observability compose configuration is invalid.'
}

docker compose -f $composeFile --profile observability up -d elasticsearch kibana edot-collector
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to start the observability profile.'
}

Wait-HttpEndpoint -Uri 'http://127.0.0.1:9209/_cluster/health' -Timeout $TimeoutSeconds
Wait-HttpEndpoint -Uri 'http://127.0.0.1:5609/api/status' -Timeout $TimeoutSeconds
Wait-HttpEndpoint -Uri 'http://127.0.0.1:13133/' -Timeout $TimeoutSeconds

[pscustomobject]@{
    Elasticsearch = 'http://127.0.0.1:9209'
    Kibana = 'http://127.0.0.1:5609'
    OtlpGrpc = 'http://127.0.0.1:4317'
    Storage = 'ephemeral tmpfs, 1 GiB Elasticsearch cap'
}
