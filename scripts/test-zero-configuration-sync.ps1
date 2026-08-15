$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "backend\src\FSM.Api\FSM.Api.csproj"
$dll = Join-Path $root "backend\src\FSM.Api\bin\Release\net9.0\FSM.Api.dll"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "fsm-zero-sync-" + [Guid]::NewGuid().ToString("N"))
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

function Invoke-Json(
    [string]$Method,
    [string]$Uri,
    [hashtable]$Headers = @{},
    $Body = $null) {
    $arguments = @{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
        UseBasicParsing = $true
    }
    if ($null -ne $Body) {
        $arguments.ContentType = "application/json"
        $arguments.Body = $Body | ConvertTo-Json -Depth 20
    }
    try { $response = Invoke-WebRequest @arguments }
    catch {
        $details = $_.ErrorDetails.Message
        $status = if ($_.Exception.Response) {
            [int]$_.Exception.Response.StatusCode
        } else { 0 }
        throw "$Method $Uri failed with HTTP $status. $details"
    }
    if ([string]::IsNullOrWhiteSpace($response.Content)) { return $null }
    return $response.Content | ConvertFrom-Json
}

function Get-StatusCode(
    [string]$Method,
    [string]$Uri,
    [hashtable]$Headers = @{},
    $Body = $null) {
    try { Invoke-Json $Method $Uri $Headers $Body | Out-Null; return 200 }
    catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        if ($_.Exception.Message -match 'HTTP (\d+)') {
            return [int]$Matches[1]
        }
        throw
    }
}

function Start-Instance(
    [string]$Edition,
    [string]$InstanceRoot,
    [int]$Port,
    [int]$DiscoveryPort,
    [string]$OfficeBaseUrl = "") {
    New-Item -ItemType Directory -Force -Path $InstanceRoot | Out-Null
    foreach ($folder in @("Documents", "Transfers", "Downloads", "Keys")) {
        New-Item -ItemType Directory -Force -Path (
            Join-Path $InstanceRoot $folder) | Out-Null
    }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = (Get-Command dotnet).Source
    $info.Arguments = "`"$dll`" --urls http://127.0.0.1:$Port"
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables["ConnectionStrings__DefaultConnection"] =
        "Data Source=$(Join-Path $InstanceRoot 'fsm.db')"
    $info.EnvironmentVariables["Storage__RootPath"] =
        (Join-Path $InstanceRoot "Documents")
    $info.EnvironmentVariables["Synchronization__TransferRootPath"] =
        (Join-Path $InstanceRoot "Transfers")
    $info.EnvironmentVariables["Synchronization__DownloadRootPath"] =
        (Join-Path $InstanceRoot "Downloads")
    $info.EnvironmentVariables["Synchronization__SchedulerPollSeconds"] = "1"
    $info.EnvironmentVariables["Synchronization__InitialRetrySeconds"] = "1"
    $info.EnvironmentVariables["Synchronization__DiscoveryTimeoutMilliseconds"] = "100"
    $info.EnvironmentVariables["Synchronization__DiscoveryPort"] = "$DiscoveryPort"
    $info.EnvironmentVariables["Synchronization__AdvertisedBaseUrl"] =
        $(if ($Edition -eq "Office") { "http://127.0.0.1:$Port" } else { "" })
    $info.EnvironmentVariables["Synchronization__OfficeBaseUrl"] = $OfficeBaseUrl
    $info.EnvironmentVariables["Synchronization__AllowInsecureHttp"] = "true"
    $info.EnvironmentVariables["DataProtection__KeyPath"] =
        (Join-Path $InstanceRoot "Keys")
    $info.EnvironmentVariables["Application__Edition"] = $Edition
    $info.EnvironmentVariables["Application__ShipVesselId"] = ""
    $info.EnvironmentVariables["Notifications__Enabled"] = "false"
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Unable to start $Edition instance." }
    $url = "http://127.0.0.1:$Port"
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        try {
            Invoke-Json Get "$url/api/synchronization/health" | Out-Null
            return @($process, $url)
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    throw "$Edition instance did not start. $($process.StandardError.ReadToEnd())"
}

function Stop-Instance([Diagnostics.Process]$Process) {
    if ($null -ne $Process -and -not $Process.HasExited) {
        $Process.Kill()
        $Process.WaitForExit()
    }
    if ($null -ne $Process) { $Process.Dispose() }
}

function Login([string]$BaseUrl, [string]$Username, [string]$Password) {
    $result = Invoke-Json Post "$BaseUrl/api/auth/login" @{} @{
        username = $Username
        password = $Password
    }
    return @{ Authorization = "Bearer $($result.token)" }
}

function Wait-Until([scriptblock]$Condition, [string]$Message) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try { if (& $Condition) { return } } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out: $Message"
}

try {
    New-Item -ItemType Directory -Force -Path $officeRoot, $shipRoot | Out-Null
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw "Backend build failed." }

    $officePort = Get-FreeTcpPort
    $shipPort = Get-FreeTcpPort
    $discoveryPort = Get-FreeTcpPort
    $office = Start-Instance "Office" $officeRoot $officePort $discoveryPort
    $officeProcess = $office[0]
    $officeUrl = $office[1]
    $ship = Start-Instance "Ship" $shipRoot $shipPort $discoveryPort $officeUrl
    $shipProcess = $ship[0]
    $shipUrl = $ship[1]

    $officeState = Invoke-Json Get "$officeUrl/api/synchronization/activation/state"
    $shipState = Invoke-Json Get "$shipUrl/api/synchronization/activation/state"
    Assert-True (-not $officeState.requiresActivation) `
        "Fresh Office must not require activation."
    Assert-True $shipState.requiresActivation `
        "Fresh Ship must open the activation workflow."

    $admin = Login $officeUrl "admin@fsm.local" "Admin123!"
    $vessel = Invoke-Json Post "$officeUrl/api/vessels" $admin @{
        vesselName = "MV ZERO CONFIG"
        imoNumber = "9876543"
        flag = "Lithuania"
    }
    Assert-True ($vessel.activationCode -match '^FSM-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$') `
        "Vessel creation must return a formatted one-time activation code."
    $officeStatus = Invoke-Json Get "$officeUrl/api/synchronization/status" $admin
    Assert-True ($officeStatus.vessels.Count -eq 1 -and
        $officeStatus.vessels[0].configured -and
        $officeStatus.vessels[0].automaticIntervalHours -eq 24) `
        "Vessel creation must automatically provision identity, credentials, and a 24-hour schedule."

    $equipment = Invoke-Json Post "$officeUrl/api/vessels/$($vessel.id)/equipment" $admin @{
        category = 0
        equipmentName = "Activation EPIRB"
        maker = "FSM"
        model = "RC20"
        serialNumber = "ZERO-001"
        quantity = 1
        inspectionBy = 0
        expiryDate = [DateTime]::UtcNow.Date.AddDays(90).ToString("yyyy-MM-dd")
        responsible = "Master"
        remarks = "Office seed"
    }
    Invoke-Json Put "$officeUrl/api/vessels/$($vessel.id)/medicine/configuration" $admin @{
        categoryAEnabled = $true
        categoryBEnabled = $true
        dangerousGoodsEnabled = $true
        warningDays = 60
    } | Out-Null
    Invoke-Json Post "$officeUrl/api/vessels/$($vessel.id)/medicine/items" $admin @{
        itemName = "Paracetamol"
        formStrength = "500 mg"
        requiredQuantity = 20
        actualQuantity = 20
        unit = "tablets"
        batchNumber = "MED-001"
        expiryDate = [DateTime]::UtcNow.Date.AddDays(365).ToString("yyyy-MM-dd")
        storageLocation = "Medical locker"
        medicalCategory = "A"
        isDangerousGoods = $false
        replacementRequired = $false
        remarks = "Initial medicine configuration"
        isActive = $true
        isStandardTemplateItem = $true
    } | Out-Null
    $crewUser = Invoke-Json Post "$officeUrl/api/users" $admin @{
        fullName = "Zero Config Crew"
        username = "zero.crew"
        email = $null
        role = 2
        isActive = $true
        password = "ZeroCrew123!"
        vesselIds = @($vessel.id)
    }
    $inspectionBody = @{
        vesselId = $vessel.id
        equipmentIds = @($equipment.id)
        format = "pdf"
        includeCertificates = $false
        inspector = "Office Test"
        results = @(@{
            equipmentId = $equipment.id
            result = "OK"
            remarks = "Activation inspection"
            correctiveAction = $null
        })
    } | ConvertTo-Json -Depth 10
    Invoke-WebRequest -Method Post -Uri "$officeUrl/api/inspection-list" `
        -Headers $admin -ContentType "application/json" -Body $inspectionBody `
        -UseBasicParsing | Out-Null

    $regenerated = Invoke-Json Post `
        "$officeUrl/api/synchronization/activation/code/$($vessel.id)" $admin
    Assert-True ((Get-StatusCode Post `
        "$officeUrl/api/synchronization/activation/connect" @{} @{
            vesselId = $vessel.id
            activationCode = $vessel.activationCode
            applicationVersion = "0.20.0-RC20"
        }) -eq 400) "Regenerating must invalidate the previous activation code."

    $catalog = Invoke-Json Get "$shipUrl/api/synchronization/activation/vessels"
    Assert-True ($catalog.vessels.Count -eq 1 -and
        $catalog.vessels[0].vesselId -eq $vessel.id) `
        "Ship must discover the Office vessel without a user-entered URL."
    $activation = Invoke-Json Post "$shipUrl/api/synchronization/activation" @{} @{
        vesselId = $vessel.id
        activationCode = $regenerated.activationCode
    }
    Assert-True ($activation.activated -and $activation.synchronized) `
        "Activation must save credentials and complete the initial synchronization."
    Assert-True ((Get-StatusCode Post `
        "$officeUrl/api/synchronization/activation/connect" @{} @{
            vesselId = $vessel.id
            activationCode = $regenerated.activationCode
            applicationVersion = "0.20.0-RC20"
        }) -eq 400) "An activation code must be invalid immediately after use."

    $shipState = Invoke-Json Get "$shipUrl/api/synchronization/activation/state"
    Assert-True (-not $shipState.requiresActivation -and
        $shipState.vessel.vesselId -eq $vessel.id) `
        "Ship activation must persist the selected local vessel."
    $crew = Login $shipUrl "zero.crew" "ZeroCrew123!"
    Assert-True ((Get-StatusCode Get `
        "$shipUrl/api/synchronization/logs" $crew) -eq 403) `
        "Ordinary Ship users must not access technical synchronization logs."
    $shipVessels = @(Invoke-Json Get "$shipUrl/api/vessels" $crew)
    $shipEquipment = @(Invoke-Json Get `
        "$shipUrl/api/vessels/$($vessel.id)/equipment" $crew)
    $shipMedicine = Invoke-Json Get `
        "$shipUrl/api/vessels/$($vessel.id)/medicine" $crew
    $shipInspections = @(Invoke-Json Get `
        "$shipUrl/api/inspection-list/vessels/$($vessel.id)/history" $crew)
    Assert-True ($shipVessels.Count -eq 1 -and $shipEquipment.Count -eq 1) `
        "Initial activation must download the vessel and equipment."
    Assert-True ($shipMedicine.items.Count -eq 1 -and
        $shipMedicine.configuration.warningDays -eq 60) `
        "Initial activation must download medicine and its configuration."
    Assert-True ($shipInspections.Count -eq 1) `
        "Initial activation must download inspection data."
    Assert-True (-not [string]::IsNullOrWhiteSpace($crewUser.id)) `
        "The assigned Office user must be created for the activation test."

    $reportFiles = @(Get-ChildItem (
        Join-Path $shipRoot "Documents\inspection-reports") -Recurse -File)
    Assert-True ($reportFiles.Count -ge 1) `
        "Initial activation must download report files."

    Invoke-Json Put `
        "$shipUrl/api/vessels/$($vessel.id)/equipment/$($equipment.id)" $crew @{
        category = 0
        equipmentName = "Activation EPIRB"
        maker = "FSM"
        model = "RC20"
        serialNumber = "ZERO-001"
        quantity = 1
        inspectionBy = 0
        expiryDate = [DateTime]::UtcNow.Date.AddDays(60).ToString("yyyy-MM-dd")
        responsible = "Master"
        remarks = "Automatic local change"
    } | Out-Null
    $serviceFolder = Join-Path $shipRoot `
        "Documents\service-requests\$($vessel.imoNumber)"
    $certificateFolder = Join-Path $shipRoot `
        "Documents\certificates\$($vessel.imoNumber)"
    New-Item -ItemType Directory -Force -Path `
        $serviceFolder, $certificateFolder | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $serviceFolder "automatic-request.xlsx"),
        [byte[]](80, 75, 3, 4, 70, 83, 77, 20))
    [IO.File]::WriteAllBytes((Join-Path $certificateFolder "automatic-certificate.pdf"),
        [Text.Encoding]::ASCII.GetBytes("%PDF-1.7 FSM certificate"))

    Wait-Until {
        $officeEquipment = @(Invoke-Json Get `
            "$officeUrl/api/vessels/$($vessel.id)/equipment" $admin)
        return $officeEquipment[0].remarks -eq "Automatic local change"
    } "Important local changes were not synchronized automatically."
    Assert-True (Test-Path (Join-Path $officeRoot `
        "Documents\service-requests\$($vessel.imoNumber)\automatic-request.xlsx")) `
        "Service request files must synchronize automatically."
    Assert-True (Test-Path (Join-Path $officeRoot `
        "Documents\certificates\$($vessel.imoNumber)\automatic-certificate.pdf")) `
        "Certificate files must synchronize automatically."

    $manual = Invoke-Json Post `
        "$shipUrl/api/synchronization/run/$($vessel.id)" $crew
    Assert-True ($manual.status -in @("Success", "Warning")) `
        "Manual synchronization must use the same operational engine."

    Stop-Instance $officeProcess
    $officeProcess = $null
    Invoke-Json Put `
        "$shipUrl/api/vessels/$($vessel.id)/equipment/$($equipment.id)" $crew @{
        category = 0
        equipmentName = "Activation EPIRB"
        maker = "FSM"
        model = "RC20"
        serialNumber = "ZERO-001"
        quantity = 1
        inspectionBy = 0
        expiryDate = [DateTime]::UtcNow.Date.AddDays(45).ToString("yyyy-MM-dd")
        responsible = "Master"
        remarks = "Offline queued change"
    } | Out-Null
    Wait-Until {
        $status = Invoke-Json Get "$shipUrl/api/synchronization/status" $crew
        return $status.vessels[0].connectionState -eq "WaitingForInternet" -and
            $status.vessels[0].retryCount -ge 1
    } "Offline mode did not enter Waiting for Internet with retry state."

    $office = Start-Instance "Office" $officeRoot $officePort $discoveryPort
    $officeProcess = $office[0]
    $officeUrl = $office[1]
    $admin = Login $officeUrl "admin@fsm.local" "Admin123!"
    Wait-Until {
        $officeEquipment = @(Invoke-Json Get `
            "$officeUrl/api/vessels/$($vessel.id)/equipment" $admin)
        return $officeEquipment[0].remarks -eq "Offline queued change"
    } "Synchronization did not resume automatically after Internet recovery."

    Invoke-Json Put "$officeUrl/api/vessels/$($vessel.id)" $admin @{
        vesselName = "MV ZERO CONFIG"
        imoNumber = "9876543"
        flag = "Startup synchronized"
        companyName = $null
        invoicingDetails = $null
        accountingEmail = $null
    } | Out-Null

    Stop-Instance $shipProcess
    $shipProcess = $null
    $ship = Start-Instance "Ship" $shipRoot $shipPort $discoveryPort $officeUrl
    $shipProcess = $ship[0]
    $shipUrl = $ship[1]
    $restartState = Invoke-Json Get "$shipUrl/api/synchronization/activation/state"
    Assert-True (-not $restartState.requiresActivation -and
        $restartState.vessel.vesselId -eq $vessel.id) `
        "Restart and newer-RC20 upgrade must preserve activation without reactivation."
    $restartCrew = Login $shipUrl "zero.crew" "ZeroCrew123!"
    Wait-Until {
        $restartVessels = @(Invoke-Json Get "$shipUrl/api/vessels" $restartCrew)
        return $restartVessels[0].flag -eq "Startup synchronized"
    } "Application startup did not initiate synchronization."

    $advanced = Invoke-Json Get "$officeUrl/api/synchronization/advanced" $admin
    Assert-True ($advanced.vessels.Count -eq 1 -and
        $advanced.healthChecks.database -eq "Available") `
        "Office Administrator diagnostics must expose endpoint, queue, retry, request, timing, and health data."

    Stop-Instance $shipProcess
    $shipProcess = $null
    Stop-Instance $officeProcess
    $officeProcess = $null
    $fixtureProject = Join-Path $root `
        "backend\tests\FSM.UpgradeFixture\FSM.UpgradeFixture.csproj"
    $rc19OfficeRoot = Join-Path $testRoot "RC19-Office"
    $rc19OfficeDatabase = Join-Path $rc19OfficeRoot "fsm.db"
    dotnet run --project $fixtureProject -c Release -- $rc19OfficeDatabase
    if ($LASTEXITCODE -ne 0) { throw "RC19 Office fixture creation failed." }
    $upgradeOfficePort = Get-FreeTcpPort
    $upgradeOffice = Start-Instance "Office" $rc19OfficeRoot `
        $upgradeOfficePort (Get-FreeTcpPort)
    $officeProcess = $upgradeOffice[0]
    $upgradeOfficeUrl = $upgradeOffice[1]
    $upgradeAdmin = Login $upgradeOfficeUrl "admin@fsm.local" "Admin123!"
    $upgradeCatalog = Invoke-Json Get `
        "$upgradeOfficeUrl/api/synchronization/activation/catalog"
    $upgradeStatus = Invoke-Json Get `
        "$upgradeOfficeUrl/api/synchronization/status" $upgradeAdmin
    Assert-True ($upgradeCatalog.vessels.Count -eq 1 -and
        $upgradeStatus.vessels[0].configured -and
        $upgradeStatus.vessels[0].automaticIntervalHours -eq 24) `
        "RC19 Office must preserve its vessel and gain automatic synchronization provisioning."
    Stop-Instance $officeProcess
    $officeProcess = $null

    $rc19ShipRoot = Join-Path $testRoot "RC19-Ship"
    $rc19ShipDatabase = Join-Path $rc19ShipRoot "fsm.db"
    dotnet run --project $fixtureProject -c Release -- $rc19ShipDatabase
    if ($LASTEXITCODE -ne 0) { throw "RC19 Ship fixture creation failed." }
    $upgradeShipPort = Get-FreeTcpPort
    $upgradeShip = Start-Instance "Ship" $rc19ShipRoot `
        $upgradeShipPort (Get-FreeTcpPort)
    $shipProcess = $upgradeShip[0]
    $upgradeShipUrl = $upgradeShip[1]
    $upgradeShipState = Invoke-Json Get `
        "$upgradeShipUrl/api/synchronization/activation/state"
    $upgradeCrew = Login $upgradeShipUrl "crew@fsm.local" "Crew123!"
    $upgradeVessels = @(Invoke-Json Get `
        "$upgradeShipUrl/api/vessels" $upgradeCrew)
    Assert-True (-not $upgradeShipState.requiresActivation -and
        $upgradeVessels.Count -eq 1 -and
        $upgradeVessels[0].imoNumber -eq "9123456") `
        "RC19 Ship must preserve its local vessel and open without reactivation."

    Write-Host "Zero-configuration synchronization integration tests passed."
    Write-Host "  Fresh Office/Ship, automatic provisioning, one-time activation"
    Write-Host "  Vessel, equipment, medicine, inspection, user, report synchronization"
    Write-Host "  Service request and certificate file synchronization"
    Write-Host "  Automatic local-change trigger, offline backoff, Internet recovery"
    Write-Host "  Manual synchronization, startup synchronization, restart persistence"
    Write-Host "  RC19 Office/Ship schema upgrade without data loss or reactivation"
}
finally {
    Stop-Instance $shipProcess
    Stop-Instance $officeProcess
    if (Test-Path $testRoot) {
        $resolvedTestRoot = (Resolve-Path $testRoot).Path
        $resolvedTemp = (Resolve-Path ([IO.Path]::GetTempPath())).Path
        if ($resolvedTestRoot.StartsWith($resolvedTemp,
            [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}
