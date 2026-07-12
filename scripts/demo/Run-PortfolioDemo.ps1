#requires -Version 7.4

[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipTcpVerification,
    [int]$StartupTimeoutSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sourceRoot = (Resolve-Path (Join-Path $root 'src')).Path
$solution = Join-Path $root 'Astra.LiveOps.slnx'
$composeFile = Join-Path $root 'deploy\docker-compose.yml'
$runtimeDirectory = Join-Path $root 'tmp-runtime\demo'
$evidenceDirectory = Join-Path $root 'output\demo'
$evidencePath = Join-Path $evidenceDirectory 'portfolio-demo-evidence.json'
$summaryPath = Join-Path $evidenceDirectory 'portfolio-demo-summary.md'
$tcpLogPath = Join-Path $evidenceDirectory 'portfolio-demo-tcp-e2e.log'
$postgresConnection = 'Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password'
$apiBaseUrl = 'http://127.0.0.1:5191'
$adminBaseUrl = 'http://127.0.0.1:5500'
$projectProcessNames = @(
    'Astra.Silo.exe',
    'Astra.Api.exe',
    'Astra.TcpGateway.exe',
    'Astra.Admin.exe',
    'Astra.Worker.exe'
)

function Test-TcpPort {
    param([int]$Port)

    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.Connect('127.0.0.1', $Port)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Wait-Until {
    param(
        [scriptblock]$Ready,
        [string]$FailureMessage,
        [int]$TimeoutSeconds = $StartupTimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (& $Ready) {
            return
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw $FailureMessage
}

function Stop-ProjectRuntime {
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
}

function Assert-RuntimePortsAvailable {
    $occupied = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object LocalPort -in 5191, 5300, 5500, 11111, 30000
    if ($occupied) {
        $details = ($occupied | ForEach-Object { "$($_.LocalPort):pid=$($_.OwningProcess)" }) -join ', '
        throw "Required demo ports are occupied by non-ASTRA processes: $details"
    }
}

function Start-AstraProcess {
    param(
        [string]$Name,
        [string]$Executable,
        [string]$WorkingDirectory,
        [hashtable]$Environment
    )

    $stdout = Join-Path $runtimeDirectory "$Name.out.log"
    $stderr = Join-Path $runtimeDirectory "$Name.err.log"
    foreach ($path in $stdout, $stderr) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path
        }
    }

    Start-Process `
        -FilePath $Executable `
        -WorkingDirectory $WorkingDirectory `
        -WindowStyle Hidden `
        -Environment $Environment `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr | Out-Null
}

function Invoke-AstraJson {
    param(
        [ValidateSet('Get', 'Post')]
        [string]$Method,
        [string]$Path,
        [object]$Body,
        [hashtable]$Headers = @{}
    )

    $parameters = @{
        Method = $Method
        Uri = $apiBaseUrl + $Path
        Headers = $Headers
        TimeoutSec = 15
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 12 -Compress
    }

    try {
        Invoke-RestMethod @parameters
    }
    catch {
        $detail = $_.ErrorDetails.Message
        throw "ASTRA request failed: $Method $Path. $detail"
    }
}

function Assert-Demo {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "Demo assertion failed: $Message"
    }
}

function Get-ElifBalance {
    param([object]$Snapshot)

    $balance = @($Snapshot.balances) | Where-Object { [int]$_.currency -eq 2 } | Select-Object -First 1
    if ($null -eq $balance) {
        return 0L
    }

    return [long]$balance.amount
}

New-Item -ItemType Directory -Force -Path $runtimeDirectory, $evidenceDirectory | Out-Null

docker info --format '{{.ServerVersion}}' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker Desktop is not ready.'
}

docker compose -f $composeFile --profile core up -d postgres | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to start the ASTRA PostgreSQL container.'
}

Wait-Until `
    -Ready {
        docker exec astra-postgres pg_isready -U astra -d astra 2>$null | Out-Null
        $LASTEXITCODE -eq 0
    } `
    -FailureMessage 'PostgreSQL did not become ready.'

Stop-ProjectRuntime
Assert-RuntimePortsAvailable

if (-not $SkipBuild) {
    dotnet build $solution -c Release --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw 'Release build failed.'
    }
}

$executables = [ordered]@{
    silo = Join-Path $root 'src\Astra.Silo\bin\Release\net10.0\Astra.Silo.exe'
    api = Join-Path $root 'src\Astra.Api\bin\Release\net10.0\Astra.Api.exe'
    tcp = Join-Path $root 'src\Astra.TcpGateway\bin\Release\net10.0\Astra.TcpGateway.exe'
    admin = Join-Path $root 'src\Astra.Admin\bin\Release\net10.0\Astra.Admin.exe'
    worker = Join-Path $root 'src\Astra.Worker\bin\Release\net10.0\Astra.Worker.exe'
}
foreach ($executable in $executables.Values) {
    if (-not (Test-Path -LiteralPath $executable)) {
        throw "Release executable is missing: $executable"
    }
}

$devSigningKey = [Convert]::ToBase64String(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$devTokenKey = [Convert]::ToBase64String(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$otlpEndpoint = if (Test-TcpPort -Port 4317) { 'http://127.0.0.1:4317' } else { '' }
$commonEnvironment = @{
    DOTNET_ENVIRONMENT = 'Development'
    ASPNETCORE_ENVIRONMENT = 'Development'
    ConnectionStrings__Postgres = $postgresConnection
    Astra__OpenTelemetry__OtlpEndpoint = $otlpEndpoint
}
$orleansEnvironment = $commonEnvironment.Clone()
$orleansEnvironment['Astra__Orleans__ClusterProvider'] = 'AdoNet'
$orleansEnvironment['Astra__Orleans__ClusterId'] = 'astra-portfolio-demo'
$orleansEnvironment['Astra__Orleans__ServiceId'] = 'astra-liveops'
$orleansEnvironment['ConnectionStrings__Orleans'] = $postgresConnection

$siloEnvironment = $orleansEnvironment.Clone()
$siloEnvironment['Astra__Orleans__AdvertisedIPAddress'] = '127.0.0.1'
$siloEnvironment['Astra__StoreProvider'] = 'PostgreSQL'
$siloEnvironment['Astra__ApplyDatabaseSchema'] = 'true'
$siloEnvironment['ConnectionStrings__Redis'] = ''
Start-AstraProcess `
    -Name 'silo' `
    -Executable $executables.silo `
    -WorkingDirectory (Join-Path $root 'src\Astra.Silo') `
    -Environment $siloEnvironment
Wait-Until -Ready { Test-TcpPort -Port 11111 } -FailureMessage 'Orleans Silo did not start.'

$apiEnvironment = $orleansEnvironment.Clone()
$apiEnvironment['ASPNETCORE_URLS'] = 'http://127.0.0.1:5191'
$apiEnvironment['Astra__LiveOpsAuth__SigningKey'] = $devSigningKey
$apiEnvironment['Astra__LiveOpsAuth__DevTokenKey'] = $devTokenKey
Start-AstraProcess `
    -Name 'api' `
    -Executable $executables.api `
    -WorkingDirectory (Join-Path $root 'src\Astra.Api') `
    -Environment $apiEnvironment

Start-AstraProcess `
    -Name 'tcp' `
    -Executable $executables.tcp `
    -WorkingDirectory (Join-Path $root 'src\Astra.TcpGateway') `
    -Environment $orleansEnvironment

$adminEnvironment = $commonEnvironment.Clone()
$adminEnvironment['ASPNETCORE_URLS'] = 'http://127.0.0.1:5500'
$adminEnvironment['Astra__ApiBaseUrl'] = $apiBaseUrl
$adminEnvironment['Astra__UiAuth__DevTokenKey'] = $devTokenKey
Start-AstraProcess `
    -Name 'admin' `
    -Executable $executables.admin `
    -WorkingDirectory (Join-Path $root 'src\Astra.Admin') `
    -Environment $adminEnvironment

$workerEnvironment = $commonEnvironment.Clone()
Start-AstraProcess `
    -Name 'worker' `
    -Executable $executables.worker `
    -WorkingDirectory (Join-Path $root 'src\Astra.Worker') `
    -Environment $workerEnvironment

Wait-Until -Ready {
    try {
        (Invoke-RestMethod -Uri "$apiBaseUrl/health/ready" -TimeoutSec 2).status -eq 'ready'
    }
    catch {
        $false
    }
} -FailureMessage 'ASTRA API did not become ready.'
Wait-Until -Ready { Test-TcpPort -Port 5300 } -FailureMessage 'TCP Gateway did not start.'
Wait-Until -Ready {
    try {
        (Invoke-WebRequest -UseBasicParsing -Uri "$adminBaseUrl/sign-in" -TimeoutSec 2).StatusCode -eq 200
    }
    catch {
        $false
    }
} -FailureMessage 'Blazor Admin did not start.'

$tcpVerified = $false
if (-not $SkipTcpVerification) {
    $previousTcpFlag = $env:ASTRA_RUN_TCP_E2E
    $previousApiFlag = $env:ASTRA_RUN_API_E2E
    $previousTokenKey = $env:ASTRA_DEV_TOKEN_KEY
    try {
        $env:ASTRA_RUN_TCP_E2E = '1'
        $env:ASTRA_RUN_API_E2E = '1'
        $env:ASTRA_DEV_TOKEN_KEY = $devTokenKey
        $tcpOutput = @(
            dotnet test `
                (Join-Path $root 'tests\Astra.IntegrationTests\Astra.IntegrationTests.csproj') `
                -c Release `
                --no-build `
                --nologo `
                --verbosity minimal `
                --filter 'FullyQualifiedName~TcpGatewayEndToEndTests' 2>&1
        )
        $tcpExitCode = $LASTEXITCODE
        [IO.File]::WriteAllLines($tcpLogPath, [string[]]$tcpOutput, [Text.UTF8Encoding]::new($false))
        Assert-Demo -Condition ($tcpExitCode -eq 0) -Message 'HTTP/TCP cross-transport E2E failed.'
        $tcpVerified = $true
    }
    finally {
        $env:ASTRA_RUN_TCP_E2E = $previousTcpFlag
        $env:ASTRA_RUN_API_E2E = $previousApiFlag
        $env:ASTRA_DEV_TOKEN_KEY = $previousTokenKey
    }
}

$token = Invoke-AstraJson `
    -Method Post `
    -Path '/api/dev/auth/token' `
    -Body @{ operatorId = 'local-supervisor' } `
    -Headers @{ 'X-Astra-Dev-Token-Key' = $devTokenKey }
$authorized = @{ Authorization = "Bearer $($token.accessToken)" }
$outboxBefore = Invoke-AstraJson -Method Get -Path '/api/admin/outbox/overview' -Body $null -Headers $authorized

$runStartedAt = [DateTimeOffset]::UtcNow
$suffix = "$($runStartedAt.ToString('yyyyMMddHHmmss'))-$([Guid]::NewGuid().ToString('N').Substring(0, 6))"
$version = "portfolio-demo-$suffix"
$bannerId = "pickup-demo-$suffix"
$playerId = [Guid]::NewGuid()
$incidentId = "incident-demo-$suffix"
$mailId = "mail-demo-$suffix"
$now = [DateTimeOffset]::UtcNow

$publish = Invoke-AstraJson -Method Post -Path '/api/admin/content/publish' -Headers $authorized -Body @{
    version = $version
    reason = 'portfolio-demo-content-publish'
    gachaBanners = @(
        @{
            bannerId = $bannerId
            costCurrency = 2
            costAmount = 100
            pityThreshold = 90
            startsAtUtc = $now.AddMinutes(-1).ToString('O')
            endsAtUtc = $now.AddHours(1).ToString('O')
            rewardPool = @(
                @{
                    kind = 1
                    rewardId = 'character-standard-demo'
                    quantity = 1
                    rarity = 2
                    weight = 9700
                    isPityTarget = $false
                    duplicateItemId = 'memory-character-standard-demo'
                    duplicateItemQuantity = 5
                },
                @{
                    kind = 1
                    rewardId = 'character-pickup-demo'
                    quantity = 1
                    rarity = 3
                    weight = 300
                    isPityTarget = $true
                    duplicateItemId = 'memory-character-pickup-demo'
                    duplicateItemQuantity = 20
                }
            )
        }
    )
}
Assert-Demo -Condition ([bool]$publish.published) -Message 'Content publish was rejected.'
$activeContent = Invoke-AstraJson -Method Get -Path '/api/admin/content/active' -Body $null -Headers $authorized
Assert-Demo -Condition ($activeContent.version -eq $version) -Message 'Published content is not active.'

$grantKey = "grant-demo-$suffix"
$grant = Invoke-AstraJson -Method Post -Path "/api/players/$playerId/wallet/grant" -Headers $authorized -Body @{
    currency = 2
    amount = 500
    reason = 'portfolio-demo-seed'
    idempotencyKey = $grantKey
    requestHash = 'server-calculates-request-hash'
}
Assert-Demo -Condition (-not [bool]$grant.replayed) -Message 'Initial wallet grant was replayed.'

$drawKey = "draw-demo-$suffix"
$drawBody = @{
    bannerId = $bannerId
    drawCount = 1
    idempotencyKey = $drawKey
    requestHash = 'server-calculates-request-hash'
}
$firstDraw = Invoke-AstraJson -Method Post -Path "/api/players/$playerId/gacha/draw" -Headers $authorized -Body $drawBody
$replayedDraw = Invoke-AstraJson -Method Post -Path "/api/players/$playerId/gacha/draw" -Headers $authorized -Body $drawBody
$drawResult = $firstDraw.responseBody | ConvertFrom-Json
Assert-Demo -Condition (-not [bool]$firstDraw.replayed) -Message 'Initial gacha draw was replayed.'
Assert-Demo -Condition ([bool]$replayedDraw.replayed) -Message 'Repeated gacha draw executed twice.'
Assert-Demo -Condition ($firstDraw.responseBody -ceq $replayedDraw.responseBody) -Message 'Gacha replay changed the response.'
Assert-Demo -Condition ((Get-ElifBalance -Snapshot $firstDraw.snapshot) -eq 400) -Message 'Gacha cost was not applied exactly once.'
Assert-Demo -Condition ($drawResult.contentVersion -eq $version) -Message 'Gacha used the wrong content version.'

$mail = Invoke-AstraJson -Method Post -Path '/api/admin/mail/incident' -Headers $authorized -Body @{
    incidentId = $incidentId
    mailId = $mailId
    title = 'Portfolio demo incident compensation'
    body = 'Compensation for the affected gacha request.'
    targetPlayerIds = @($playerId)
    rewards = @(@{ currency = 2; amount = 200 })
    reason = 'portfolio-demo-compensation'
}
Assert-Demo -Condition ($mail.mailId -eq $mailId) -Message 'Incident mail was not created.'
$target = Invoke-AstraJson `
    -Method Get `
    -Path "/api/admin/mail/$mailId/targets/$playerId" `
    -Body $null `
    -Headers $authorized
Assert-Demo -Condition ([bool]$target.targeted) -Message 'Affected player is missing from the compensation snapshot.'

$claimBody = @{
    mailId = $mailId
    idempotencyKey = "claim-demo-$suffix"
    requestHash = 'server-calculates-request-hash'
}
$firstClaim = Invoke-AstraJson -Method Post -Path "/api/players/$playerId/mail/claim" -Headers $authorized -Body $claimBody
$replayedClaim = Invoke-AstraJson -Method Post -Path "/api/players/$playerId/mail/claim" -Headers $authorized -Body $claimBody
$finalWallet = Invoke-AstraJson -Method Get -Path "/api/players/$playerId/wallet" -Body $null -Headers $authorized
Assert-Demo -Condition (-not [bool]$firstClaim.replayed) -Message 'Initial mail claim was replayed.'
Assert-Demo -Condition ([bool]$replayedClaim.replayed) -Message 'Repeated mail claim paid twice.'
Assert-Demo -Condition ($firstClaim.responseBody -ceq $replayedClaim.responseBody) -Message 'Mail claim replay changed the response.'
Assert-Demo -Condition ((Get-ElifBalance -Snapshot $finalWallet) -eq 600) -Message 'Final balance does not match one draw and one compensation.'

$outboxAfter = $null
Wait-Until -TimeoutSeconds 20 -Ready {
    $script:outboxAfter = Invoke-AstraJson -Method Get -Path '/api/admin/outbox/overview' -Body $null -Headers $authorized
    [long]$script:outboxAfter.publishedCount -gt [long]$outboxBefore.publishedCount
} -FailureMessage 'Outbox Worker did not publish the demo events.'

$audits = @(Invoke-AstraJson -Method Get -Path '/api/admin/audit?limit=200' -Body $null -Headers $authorized)
$demoAudits = @($audits | Where-Object {
    $_.targetId -eq $version -or $_.targetId -eq $mailId -or $_.targetId -eq $playerId.ToString('D')
})
$requiredActions = @('content.publish', 'wallet.grant', 'gacha.draw', 'mail.incident.create', 'mail.claim')
$observedActions = @($demoAudits | ForEach-Object { $_.action })
foreach ($action in $requiredActions) {
    Assert-Demo -Condition ($action -in $observedActions) -Message "Audit action is missing: $action"
}

$evidence = [ordered]@{
    schemaVersion = 1
    runId = $suffix
    startedAtUtc = $runStartedAt
    completedAtUtc = [DateTimeOffset]::UtcNow
    runtime = [ordered]@{
        api = $apiBaseUrl
        admin = $adminBaseUrl
        postgres = 'astra-postgres:17-alpine'
        redisMode = 'disabled; PostgreSQL fallback exercised'
        tcpGatewayPort = 5300
    }
    content = [ordered]@{
        version = $version
        checksum = $activeContent.checksum
        bannerId = $bannerId
        active = $true
    }
    player = [ordered]@{
        playerId = $playerId
        initialGrant = 500
        gachaCost = 100
        compensation = 200
        finalElifBalance = $(Get-ElifBalance -Snapshot $finalWallet)
        ledgerVersion = [long]$finalWallet.ledgerVersion
    }
    gacha = [ordered]@{
        firstRequestReplayed = [bool]$firstDraw.replayed
        retryReplayed = [bool]$replayedDraw.replayed
        exactResponseReplay = $firstDraw.responseBody -ceq $replayedDraw.responseBody
        contentVersion = $drawResult.contentVersion
        contentChecksum = $drawResult.contentChecksum
        rewards = @($drawResult.rewards | ForEach-Object {
            [ordered]@{
                rewardId = $_.rewardId
                rarity = [int]$_.rarity
                wasDuplicate = [bool]$_.wasDuplicate
            }
        })
    }
    compensation = [ordered]@{
        incidentId = $incidentId
        mailId = $mailId
        targetSnapshotMatched = [bool]$target.targeted
        firstClaimReplayed = [bool]$firstClaim.replayed
        retryReplayed = [bool]$replayedClaim.replayed
        exactResponseReplay = $firstClaim.responseBody -ceq $replayedClaim.responseBody
    }
    operations = [ordered]@{
        requiredAuditActions = $requiredActions
        matchingAuditRows = $demoAudits.Count
        outboxPublishedDelta = [long]$outboxAfter.publishedCount - [long]$outboxBefore.publishedCount
        outboxPending = [long]$outboxAfter.pendingCount
        outboxDeadLetters = [long]$outboxAfter.deadLetterCount
    }
    tcp = [ordered]@{
        crossTransportReplayVerified = $tcpVerified
        skipped = [bool]$SkipTcpVerification
        log = if ($SkipTcpVerification) { $null } else { 'output/demo/portfolio-demo-tcp-e2e.log' }
    }
    checks = [ordered]@{
        contentPublishAndActivation = $true
        gachaAtomicityAndReplay = $true
        incidentTargetSnapshot = $true
        mailClaimReplay = $true
        auditCoverage = $true
        outboxDelivery = $true
        tcpCrossTransport = $tcpVerified
    }
}

[IO.File]::WriteAllText(
    $evidencePath,
    ($evidence | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
$summary = @"
# ASTRA Portfolio Demo Evidence

- Run: $suffix
- Completed: $($evidence.completedAtUtc.ToString('O'))
- Active content: $version
- Player: $playerId

| Verification | Result |
|---|---:|
| Gacha retry executed once | PASS |
| Exact gacha response replay | PASS |
| Incident target snapshot | PASS |
| Mail retry paid once | PASS |
| Final Elif balance (500 - 100 + 200) | $($evidence.player.finalElifBalance) |
| Required audit actions | PASS |
| Outbox published delta | $($evidence.operations.outboxPublishedDelta) |
| HTTP/TCP cross-transport replay | $(if ($tcpVerified) { 'PASS' } elseif ($SkipTcpVerification) { 'SKIPPED' } else { 'FAIL' }) |

Runtime: [Admin]($adminBaseUrl) | [API readiness]($apiBaseUrl/health/ready)
"@
[IO.File]::WriteAllText($summaryPath, $summary, [Text.UTF8Encoding]::new($false))

Write-Host "PASS: ASTRA portfolio demo completed."
Write-Host "Evidence: $evidencePath"
Write-Host "Admin: $adminBaseUrl"

[pscustomobject]@{
    Result = 'PASS'
    Evidence = $evidencePath
    Summary = $summaryPath
    Admin = $adminBaseUrl
    Api = "$apiBaseUrl/health/ready"
    FinalElifBalance = $evidence.player.finalElifBalance
    TcpCrossTransport = if ($tcpVerified) { 'PASS' } else { 'SKIPPED' }
}
