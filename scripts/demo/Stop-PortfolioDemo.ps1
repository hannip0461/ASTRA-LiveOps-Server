#requires -Version 7.4

[CmdletBinding()]
param(
    [switch]$StopPostgres
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sourceRoot = (Resolve-Path (Join-Path $root 'src')).Path
$projectProcessNames = @(
    'Astra.Silo.exe',
    'Astra.Api.exe',
    'Astra.TcpGateway.exe',
    'Astra.Admin.exe',
    'Astra.Worker.exe'
)
$targets = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -in $projectProcessNames -and
    $_.ExecutablePath -and
    $_.ExecutablePath.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase)
}

foreach ($target in $targets) {
    Stop-Process -Id $target.ProcessId -ErrorAction SilentlyContinue
}

if ($targets) {
    Start-Sleep -Seconds 1
}

foreach ($target in $targets) {
    if (Get-Process -Id $target.ProcessId -ErrorAction SilentlyContinue) {
        Stop-Process -Id $target.ProcessId -Force
    }
}

if ($StopPostgres) {
    docker compose `
        -f (Join-Path $root 'deploy\docker-compose.yml') `
        --profile core `
        stop postgres | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to stop the ASTRA PostgreSQL container.'
    }
}

[pscustomobject]@{
    StoppedProjectProcesses = @($targets).Count
    PostgresStopped = [bool]$StopPostgres
    VolumesDeleted = $false
}
