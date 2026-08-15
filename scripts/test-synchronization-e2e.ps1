$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "backend\src\FSM.Api\FSM.Api.csproj"
$dll = Join-Path $root "backend\src\FSM.Api\bin\Release\net9.0\FSM.Api.dll"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("fsm-sync-e2e-" + [Guid]::NewGuid().ToString("N"))
$officeRoot = Join-Path $testRoot "Office"
$shipRoot = Join-Path $testRoot "Ship"
$officeProcess = $null
$shipProcess = $null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Invoke-Json([string]$Method, [string]$Uri, [hashtable]$Headers = @{}, $Body = $null) {
    $arguments = @{ Method = $Method; Uri = $Uri; Headers = $Headers; UseBasicParsing = $true }
    if ($null -ne $Body) {
        $arguments.ContentType = "application/json"
        $arguments.Body = $Body | ConvertTo-Json -Depth 20
    }
    try { $response = Invoke-WebRequest @arguments }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        $details = $_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($details) -and $_.Exception.Response) {
            $reader = [IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
            try { $details = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        throw "$Method $Uri failed with HTTP $status. $details"
    }
    if ([string]::IsNullOrWhiteSpace($response.Content)) { return $null }
    return $response.Content | ConvertFrom-Json
}

function Get-StatusCode([string]$Method, [string]$Uri, [hashtable]$Headers = @{}, $Body = $null) {
    try { Invoke-Json $Method $Uri $Headers $Body | Out-Null; return 200 }
    catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        if ($_.Exception.Message -match 'HTTP (\d+)') { return [int]$Matches[1] }
        throw
    }
}

function Start-Instance([string]$Edition, [string]$InstanceRoot, [int]$Port, [string]$ShipVesselId = "") {
    New-Item -ItemType Directory -Force -Path $InstanceRoot | Out-Null
    foreach ($folder in @("Documents", "Transfers", "Downloads", "Keys")) {
        New-Item -ItemType Directory -Force -Path (Join-Path $InstanceRoot $folder) | Out-Null
    }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = (Get-Command dotnet).Source
    $info.Arguments = "`"$dll`" --urls http://127.0.0.1:$Port"
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables["ConnectionStrings__DefaultConnection"] = "Data Source=$(Join-Path $InstanceRoot 'fsm.db')"
    $info.EnvironmentVariables["Storage__RootPath"] = (Join-Path $InstanceRoot "Documents")
    $info.EnvironmentVariables["Synchronization__TransferRootPath"] = (Join-Path $InstanceRoot "Transfers")
    $info.EnvironmentVariables["Synchronization__DownloadRootPath"] = (Join-Path $InstanceRoot "Downloads")
    $info.EnvironmentVariables["Synchronization__SchedulerPollSeconds"] = "1"
    $info.EnvironmentVariables["DataProtection__KeyPath"] = (Join-Path $InstanceRoot "Keys")
    $info.EnvironmentVariables["Application__Edition"] = $Edition
    $info.EnvironmentVariables["Application__ShipVesselId"] = $ShipVesselId
    $info.EnvironmentVariables["Notifications__Enabled"] = "false"
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $info
    if (-not $process.Start()) { throw "Unable to start $Edition instance." }
    $url = "http://127.0.0.1:$Port"
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        try {
            $health = Invoke-Json Get "$url/api/synchronization/health"
            return @($process, $url, $health)
        } catch { Start-Sleep -Milliseconds 250 }
    }
    $errorText = $process.StandardError.ReadToEnd()
    throw "$Edition instance did not start. $errorText"
}

function Stop-Instance([Diagnostics.Process]$Process) {
    if ($null -ne $Process -and -not $Process.HasExited) { $Process.Kill(); $Process.WaitForExit() }
    if ($null -ne $Process) { $Process.Dispose() }
}

function Login([string]$BaseUrl, [string]$Username, [string]$Password) {
    $login = Invoke-Json Post "$BaseUrl/api/auth/login" @{} @{ username = $Username; password = $Password }
    return @{ Authorization = "Bearer $($login.token)" }
}

try {
    New-Item -ItemType Directory -Force -Path $officeRoot, $shipRoot | Out-Null
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw "Backend build failed." }

    $officePort = Get-FreeTcpPort; $shipPort = Get-FreeTcpPort
    $office = Start-Instance "Office" $officeRoot $officePort
    $officeProcess = $office[0]; $officeUrl = $office[1]
    Assert-True ($office[2].applicationEdition -eq "Office" -and $office[2].databaseAvailable) `
        "Office health endpoint must report a usable Office database."
    $admin = Login $officeUrl "admin@fsm.local" "Admin123!"
    $vessel = Invoke-Json Post "$officeUrl/api/vessels" $admin @{
        vesselName = "MV SYNC E2E"; imoNumber = "9876543"; flag = "Lithuania"
    }
    $seeded = Invoke-Json Post "$officeUrl/api/vessels/$($vessel.id)/equipment" $admin @{
        category = 0; equipmentName = "Seeded EPIRB"; maker = "FSM"; model = "RC20"
        serialNumber = "SYNC-SEED"; quantity = 1; inspectionBy = 0
        expiryDate = [DateTime]::UtcNow.Date.AddDays(120).ToString("yyyy-MM-dd")
        responsible = "Master"; remarks = "Seeded on both sides"
    }
    Stop-Instance $officeProcess; $officeProcess = $null
    Copy-Item (Join-Path $officeRoot "fsm.db*") $shipRoot -Force

    $office = Start-Instance "Office" $officeRoot $officePort
    $officeProcess = $office[0]; $officeUrl = $office[1]
    $admin = Login $officeUrl "admin@fsm.local" "Admin123!"
    Invoke-Json Put "$officeUrl/api/synchronization/settings/$($vessel.id)" $admin @{
        automaticIntervalHours = 0; remoteBaseUrl = $officeUrl; enabled = $true; allowInsecureHttp = $true
    } | Out-Null
    $token = (Invoke-Json Post "$officeUrl/api/synchronization/token/$($vessel.id)" $admin).token
    $configurationPassphrase = "RC20-E2E-Config!"
    $configurationResponse = Invoke-WebRequest -Method Post `
        -Uri "$officeUrl/api/synchronization/configuration/export/$($vessel.id)" `
        -Headers $admin -ContentType "application/json" `
        -Body (@{ passphrase = $configurationPassphrase } | ConvertTo-Json) `
        -UseBasicParsing
    $protectedConfiguration = [string]$configurationResponse.Content
    Assert-True (-not $protectedConfiguration.Contains($token)) `
        "Exported Ship configuration must not contain the plaintext token."
    Write-Host "PASS: protected Ship configuration export"
    $newUser = Invoke-Json Post "$officeUrl/api/users" $admin @{
        fullName = "E2E Ship Crew"; username = "e2e.crew"; email = $null
        role = 2; isActive = $true; password = "SyncUser123!"; vesselIds = @($vessel.id)
    }

    $ship = Start-Instance "Ship" $shipRoot $shipPort ([string]$vessel.id)
    $shipProcess = $ship[0]; $shipUrl = $ship[1]
    $crew = Login $shipUrl "crew@fsm.local" "Crew123!"
    $shipVessels = @(Invoke-Json Get "$shipUrl/api/vessels" $crew)
    Assert-True ($shipVessels.Count -eq 1 -and $shipVessels[0].id -eq $vessel.id -and
        $shipVessels[0].imoNumber -eq $vessel.imoNumber) `
        "Ship database must contain the configured vessel ID and IMO."
    Invoke-Json Post "$shipUrl/api/synchronization/configuration/import" $crew @{
        protectedConfiguration = $protectedConfiguration
        passphrase = $configurationPassphrase
    } | Out-Null
    Write-Host "PASS: protected Ship configuration import"
    Assert-True ((Get-StatusCode Put "$shipUrl/api/synchronization/configuration/manual" $crew @{
        vesselId = [Guid]::NewGuid(); vesselImo = $vessel.imoNumber; officeEndpointUrl = $officeUrl
        synchronizationToken = $token; automaticIntervalHours = 0; allowInsecureHttp = $true
    }) -eq 403) "Ship must reject configuration for a different local vessel."
    $connection = Invoke-Json Post "$shipUrl/api/synchronization/test/$($vessel.id)" $crew
    Assert-True ($connection.connected -and $connection.vesselImo -eq $vessel.imoNumber) `
        "Ship Test Connection must validate the configured vessel and IMO."

    $syncHeaders = @{
        "X-FSM-Synchronization-Token" = $token
        "X-Forwarded-Proto" = "https"
    }
    $handshakeUrl = "$officeUrl/api/synchronization/handshake?vesselId=$($vessel.id)&vesselImo=$($vessel.imoNumber)&version=0.20.0-RC20"
    Assert-True ((Get-StatusCode Get $handshakeUrl @{ "X-FSM-Synchronization-Token" = "invalid" }) -eq 401) `
        "Invalid vessel token must return HTTP 401."
    $wrongVesselUrl = "$officeUrl/api/synchronization/handshake?vesselId=$([Guid]::NewGuid())&vesselImo=$($vessel.imoNumber)&version=0.20.0-RC20"
    $wrongVesselStatus = Get-StatusCode Get $wrongVesselUrl $syncHeaders
    Assert-True ($wrongVesselStatus -in @(401, 403)) `
        "A token must not authorize another vessel."
    $wrongImoUrl = "$officeUrl/api/synchronization/handshake?vesselId=$($vessel.id)&vesselImo=0000000&version=0.20.0-RC20"
    Assert-True ((Get-StatusCode Get $wrongImoUrl $syncHeaders) -eq 403) `
        "A mismatched vessel IMO must be rejected."

    $pdfFolder = Join-Path $shipRoot "Documents\reports\$($vessel.imoNumber)"
    $xlsxFolder = Join-Path $shipRoot "Documents\service-requests\$($vessel.imoNumber)"
    New-Item -ItemType Directory -Force -Path $pdfFolder, $xlsxFolder | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $pdfFolder "sync-report.pdf"), [Text.Encoding]::ASCII.GetBytes("%PDF-1.7`nFSM sync e2e"))
    [IO.File]::WriteAllBytes((Join-Path $xlsxFolder "sync-request.xlsx"), [byte[]](80,75,3,4,70,83,77,20))

    $expiry = [DateTime]::UtcNow.Date.AddDays(15).ToString("yyyy-MM-dd")
    Invoke-Json Put "$shipUrl/api/vessels/$($vessel.id)/equipment/$($seeded.id)" $crew @{
        category = 0; equipmentName = "Seeded EPIRB"; maker = "FSM"; model = "RC20"
        serialNumber = "SYNC-SEED"; quantity = 1; inspectionBy = 0; expiryDate = $expiry
        responsible = "Master"; remarks = "Ship authoritative update"
    } | Out-Null
    $first = Invoke-Json Post "$shipUrl/api/synchronization/run/$($vessel.id)" $crew
    Assert-True ($first.status -in @("Success", "Warning")) "First synchronization must complete."
    $officeEquipment = @(Invoke-Json Get "$officeUrl/api/vessels/$($vessel.id)/equipment" $admin)
    Assert-True ($officeEquipment.Count -eq 1) "First synchronization must not duplicate seeded equipment."
    Assert-True ($officeEquipment[0].expiryDate -eq $expiry) "Ship expiry-date change must appear in Office."
    $shipUserLogin = Invoke-Json Post "$shipUrl/api/auth/login" @{} @{ username = "e2e.crew"; password = "SyncUser123!" }
    Assert-True (-not [string]::IsNullOrWhiteSpace($shipUserLogin.token)) "Office user and password hash must synchronize to Ship."
    Write-Host "PASS: first synchronization and ownership rules"

    foreach ($relative in @("reports\$($vessel.imoNumber)\sync-report.pdf", "service-requests\$($vessel.imoNumber)\sync-request.xlsx")) {
        $shipFile = Join-Path (Join-Path $shipRoot "Documents") $relative
        $officeFile = Join-Path (Join-Path $officeRoot "Documents") $relative
        Assert-True (Test-Path $officeFile) "Synchronized document is missing in Office: $relative"
        Assert-True ((Get-FileHash $shipFile -Algorithm SHA256).Hash -eq (Get-FileHash $officeFile -Algorithm SHA256).Hash) `
            "Synchronized document SHA-256 mismatch: $relative"
    }

    $resumeBytes = [Text.Encoding]::UTF8.GetBytes("resume-transfer-content")
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $resumeChecksum = ([BitConverter]::ToString($sha256.ComputeHash($resumeBytes))).Replace("-", "") }
    finally { $sha256.Dispose() }
    $resumeFile = @{ relativePath = "reports/$($vessel.imoNumber)/resume.pdf"; size = $resumeBytes.Length; checksum = $resumeChecksum; lastModifiedUtc = [DateTime]::UtcNow.ToString("O") }
    $prepared = Invoke-Json Post "$officeUrl/api/synchronization/file-transfers/prepare" $syncHeaders @{
        applicationVersion = "0.20.0-RC20"; vesselId = $vessel.id; file = $resumeFile
    }
    $half = [Math]::Floor($resumeBytes.Length / 2)
    $firstChunk = [byte[]]$resumeBytes[0..($half-1)]
    Invoke-WebRequest -Method Put -Uri "$officeUrl/api/synchronization/file-transfers/$($prepared.transferId)/chunks?vesselId=$($vessel.id)&offset=0&version=0.20.0-RC20" `
        -Headers $syncHeaders -ContentType "application/octet-stream" -Body $firstChunk -UseBasicParsing | Out-Null
    $resumed = Invoke-Json Post "$officeUrl/api/synchronization/file-transfers/prepare" $syncHeaders @{
        applicationVersion = "0.20.0-RC20"; vesselId = $vessel.id; file = $resumeFile
    }
    Assert-True ($resumed.nextOffset -eq $half) "Interrupted upload must resume from the acknowledged offset."

    $repeat = Invoke-Json Post "$shipUrl/api/synchronization/run/$($vessel.id)" $crew
    Assert-True ($repeat.uploadedItems -eq 0 -and $repeat.downloadedItems -eq 0) `
        "A repeat no-change synchronization must transfer zero items."
    Write-Host "PASS: repeat no-change synchronization"

    Invoke-Json Put "$shipUrl/api/vessels/$($vessel.id)/equipment/$($seeded.id)" $crew @{
        category = 0; equipmentName = "Seeded EPIRB"; maker = "FSM"; model = "RC20"
        serialNumber = "SYNC-SEED"; quantity = 1; inspectionBy = 0; expiryDate = $expiry
        responsible = "Master"; remarks = "Automatic scheduler update"
    } | Out-Null
    Invoke-Json Put "$shipUrl/api/synchronization/settings/$($vessel.id)" $crew @{
        automaticIntervalHours = 6; remoteBaseUrl = $officeUrl; enabled = $true; allowInsecureHttp = $true
    } | Out-Null
    $automaticApplied = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 500
        $currentOfficeItem = @(Invoke-Json Get "$officeUrl/api/vessels/$($vessel.id)/equipment" $admin)[0]
        if ($currentOfficeItem.remarks -eq "Automatic scheduler update") { $automaticApplied = $true; break }
    }
    Assert-True $automaticApplied "Automatic synchronization must run at the configured interval."
    Write-Host "PASS: automatic synchronization"
    Invoke-Json Put "$shipUrl/api/synchronization/settings/$($vessel.id)" $crew @{
        automaticIntervalHours = 0; remoteBaseUrl = $officeUrl; enabled = $true; allowInsecureHttp = $true
    } | Out-Null

    $rotated = (Invoke-Json Post "$officeUrl/api/synchronization/token/$($vessel.id)" $admin).token
    Assert-True ((Get-StatusCode Post "$shipUrl/api/synchronization/test/$($vessel.id)" $crew) -eq 502) `
        "Token rotation must invalidate the previous Ship configuration."
    Invoke-Json Put "$shipUrl/api/synchronization/configuration/manual" $crew @{
        vesselId = $vessel.id; vesselImo = $vessel.imoNumber; officeEndpointUrl = $officeUrl
        synchronizationToken = "  $rotated  "; automaticIntervalHours = 0; allowInsecureHttp = $true
    } | Out-Null
    Invoke-Json Post "$shipUrl/api/synchronization/test/$($vessel.id)" $crew | Out-Null
    Write-Host "PASS: token rotation"

    Stop-Instance $officeProcess; $officeProcess = $null
    Assert-True ((Get-StatusCode Post "$shipUrl/api/synchronization/run/$($vessel.id)" $crew) -eq 503) `
        "Ship must remain operational and report a safe failure while Office is offline."
    $office = Start-Instance "Office" $officeRoot $officePort
    $officeProcess = $office[0]; $officeUrl = $office[1]
    $admin = Login $officeUrl "admin@fsm.local" "Admin123!"
    Invoke-Json Post "$shipUrl/api/synchronization/run/$($vessel.id)" $crew | Out-Null
    Write-Host "PASS: offline recovery"

    Stop-Instance $shipProcess; $shipProcess = $null
    $ship = Start-Instance "Ship" $shipRoot $shipPort ([string]$vessel.id)
    $shipProcess = $ship[0]; $shipUrl = $ship[1]; $crew = Login $shipUrl "crew@fsm.local" "Crew123!"
    $persistedStatus = Invoke-Json Get "$shipUrl/api/synchronization/status" $crew
    Assert-True ($persistedStatus.vessels[0].configured -and $persistedStatus.vessels[0].lastSynchronizationUtc) `
        "Ship restart must preserve synchronization configuration and cursors."
    Write-Host "PASS: restart persistence"

    Invoke-Json Put "$officeUrl/api/synchronization/settings/$($vessel.id)" $admin @{
        automaticIntervalHours = 6; remoteBaseUrl = "https://office.invalid:8443"; enabled = $true; allowInsecureHttp = $false
    } | Out-Null
    $newHeaders = @{ "X-FSM-Synchronization-Token" = $rotated }
    Assert-True ((Get-StatusCode Get $handshakeUrl $newHeaders) -eq 426) `
        "Production mode must enforce HTTPS."
    Invoke-Json Put "$officeUrl/api/synchronization/settings/$($vessel.id)" $admin @{
        automaticIntervalHours = 6; remoteBaseUrl = $officeUrl; enabled = $true; allowInsecureHttp = $true
    } | Out-Null
    Assert-True ((Get-StatusCode Get $handshakeUrl $newHeaders) -eq 200) `
        "Explicit local HTTP test mode must allow synchronization."

    $diagnostics = Invoke-WebRequest -Uri "$officeUrl/api/synchronization/diagnostics" -Headers $admin -UseBasicParsing
    Assert-True ($diagnostics.RawContentLength -gt 0) "Support diagnostics ZIP must be generated."
    $diagnosticText = [Text.Encoding]::UTF8.GetString($diagnostics.Content)
    Assert-True (-not $diagnosticText.Contains($rotated)) "Diagnostics must not contain synchronization tokens."

    Write-Host "Synchronization end-to-end tests passed."
    Write-Host "  Office health, Ship connection test, token/vessel/IMO validation"
    Write-Host "  First/incremental/no-change exchange and ownership rules"
    Write-Host "  PDF/XLSX SHA-256 transfer, interrupted upload resume"
    Write-Host "  Offline recovery, token rotation, restart persistence"
    Write-Host "  HTTPS enforcement, explicit local HTTP mode, sanitized diagnostics"
}
finally {
    Stop-Instance $shipProcess; Stop-Instance $officeProcess
    if (Test-Path $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
