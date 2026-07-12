$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$composeFile = Join-Path $root 'deploy\docker-compose.yml'

docker compose -f $composeFile --profile observability rm -f -s -v edot-collector kibana elasticsearch
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to remove the observability containers.'
}

Write-Output 'Observability containers removed. Ephemeral telemetry data was released; pinned images remain.'
