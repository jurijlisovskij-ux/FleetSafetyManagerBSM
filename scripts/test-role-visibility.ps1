$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$apiProject = Join-Path $root "backend\src\FSM.Api\FSM.Api.csproj"
$apiDll = Join-Path $root "backend\src\FSM.Api\bin\Release\net9.0\FSM.Api.dll"
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "fsm-role-visibility-" + [Guid]::NewGuid().ToString("N"))
$databasePath = Join-Path $testRoot "fsm.db"
$documentsPath = Join-Path $testRoot "Documents"
$logPath = Join-Path $testRoot "api.log"
$errorLogPath = Join-Path $testRoot "api-error.log"
$balticVesselId = $null
$adminPassword = "Admin123!"
$process = $null

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Get-FreeTcpPort {
    $listener = New-Object System.Net.Sockets.TcpListener(
        [System.Net.IPAddress]::Loopback,
        0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Invoke-Json {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers = @{},
        $Body = $null
    )

    $arguments = @{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
        UseBasicParsing = $true
    }

    if ($null -ne $Body) {
        $arguments.ContentType = "application/json"
        $arguments.Body = $Body | ConvertTo-Json -Depth 8
    }

    $response = Invoke-WebRequest @arguments
    if ([string]::IsNullOrWhiteSpace($response.Content)) {
        return $null
    }

    return $response.Content | ConvertFrom-Json
}

function Invoke-FileDownload {
    param(
        [string]$Uri,
        [hashtable]$Headers,
        $Body
    )

    return Invoke-WebRequest `
        -Method Post `
        -Uri $Uri `
        -Headers $Headers `
        -ContentType "application/json" `
        -Body ($Body | ConvertTo-Json -Depth 8) `
        -UseBasicParsing
}

function Get-AuthorizationHeader {
    param(
        [string]$BaseUrl,
        [string]$Email,
        [string]$Password
    )

    $login = Invoke-Json `
        -Method Post `
        -Uri "$BaseUrl/api/auth/login" `
        -Body @{ email = $Email; password = $Password }

    return @{ Authorization = "Bearer $($login.token)" }
}

function Get-StatusCode {
    param(
        [string]$Uri,
        [hashtable]$Headers
    )

    try {
        Invoke-WebRequest `
            -Method Get `
            -Uri $Uri `
            -Headers $Headers `
            -UseBasicParsing | Out-Null
        return 200
    }
    catch {
        if ($null -eq $_.Exception.Response) {
            throw
        }

        return [int]$_.Exception.Response.StatusCode
    }
}

function Start-FsmApi {
    param(
        [ValidateSet("Office", "Ship")]
        [string]$Edition,
        [string]$ShipVesselId,
        [int]$Port
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = (Get-Command dotnet).Source
    $startInfo.Arguments = "`"$apiDll`" --urls http://127.0.0.1:$Port"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables["ConnectionStrings__DefaultConnection"] =
        "Data Source=$databasePath"
    $startInfo.EnvironmentVariables["Storage__RootPath"] = $documentsPath
    $startInfo.EnvironmentVariables["Application__Edition"] = $Edition
    $startInfo.EnvironmentVariables["Application__ShipVesselId"] = $ShipVesselId
    $startInfo.EnvironmentVariables["Notifications__Enabled"] = "false"

    $startedProcess = New-Object System.Diagnostics.Process
    $startedProcess.StartInfo = $startInfo
    if (-not $startedProcess.Start()) {
        throw "Unable to start the $Edition API."
    }

    $baseUrl = "http://127.0.0.1:$Port"
    for ($attempt = 1; $attempt -le 40; $attempt++) {
        if ($startedProcess.HasExited) {
            throw "$Edition API exited with code $($startedProcess.ExitCode)."
        }

        try {
            Invoke-WebRequest `
                -Uri "$baseUrl/swagger/v1/swagger.json" `
                -UseBasicParsing | Out-Null
            return @($startedProcess, $baseUrl)
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }

    throw "$Edition API did not become ready."
}

function Stop-FsmApi {
    param([System.Diagnostics.Process]$ApiProcess)

    if ($null -eq $ApiProcess) {
        return
    }

    if (-not $ApiProcess.HasExited) {
        $ApiProcess.Kill()
        $ApiProcess.WaitForExit()
    }

    $ApiProcess.StandardOutput.ReadToEnd() | Set-Content $logPath
    $ApiProcess.StandardError.ReadToEnd() | Set-Content $errorLogPath
    $ApiProcess.Dispose()
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $documentsPath -Force | Out-Null

    dotnet build $apiProject -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Backend build failed with exit code $LASTEXITCODE."
    }

    $officePort = Get-FreeTcpPort
    $office = Start-FsmApi `
        -Edition Office `
        -ShipVesselId "" `
        -Port $officePort
    $process = $office[0]
    $baseUrl = $office[1]
    $adminHeaders = Get-AuthorizationHeader `
        -BaseUrl $baseUrl `
        -Email "admin@fsm.local" `
        -Password $adminPassword

    $temporaryUser = Invoke-Json -Method Post -Uri "$baseUrl/api/users" -Headers $adminHeaders -Body @{
        fullName = "Temporary Superintendent"
        email = "temporary@fsm.local"
        role = 1
        isActive = $true
        password = "Temporary123!"
        vesselIds = @()
        forcePasswordChange = $true
    }
    $temporaryLogin = Invoke-Json -Method Post -Uri "$baseUrl/api/auth/login" -Body @{
        email = "temporary@fsm.local"; password = "Temporary123!"
    }
    Assert-True (-not $temporaryLogin.forcePasswordChange) `
        "Passwords assigned by OfficeAdmin must not be temporary."
    $temporaryHeaders = @{ Authorization = "Bearer $($temporaryLogin.token)" }
    $selfChangeStatus = try {
        Invoke-Json -Method Post -Uri "$baseUrl/api/auth/change-password" -Headers $temporaryHeaders -Body @{
            currentPassword = "Temporary123!"; newPassword = "Changed123!"
        } | Out-Null; 200
    } catch { [int]$_.Exception.Response.StatusCode }
    Assert-True ([int]$selfChangeStatus -eq 404) `
        "Ordinary users must not have a self-service password change endpoint."
    Invoke-Json -Method Post -Uri "$baseUrl/api/users/$($temporaryUser.id)/reset-password" -Headers $adminHeaders -Body @{
        password = "Reset123!"
    } | Out-Null
    $resetLogin = Invoke-Json -Method Post -Uri "$baseUrl/api/auth/login" -Body @{
        email = "temporary@fsm.local"; password = "Reset123!"
    }
    Assert-True (-not $resetLogin.forcePasswordChange) `
        "Administrator-assigned passwords must be immediately final."
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try { Invoke-Json -Method Post -Uri "$baseUrl/api/auth/login" -Body @{
            email = "temporary@fsm.local"; password = "Wrong123!"
        } | Out-Null } catch { }
    }
    $lockedStatus = try {
        Invoke-Json -Method Post -Uri "$baseUrl/api/auth/login" -Body @{
            email = "temporary@fsm.local"; password = "Reset123!"
        } | Out-Null; 200
    } catch { [int]$_.Exception.Response.StatusCode }
    Assert-True ([int]$lockedStatus -eq 401) "Locked user must not sign in."
    Invoke-Json -Method Post -Uri "$baseUrl/api/users/$($temporaryUser.id)/unlock" -Headers $adminHeaders | Out-Null
    $unlockedLogin = Invoke-Json -Method Post -Uri "$baseUrl/api/auth/login" -Body @{
        email = "temporary@fsm.local"; password = "Reset123!"
    }
    Assert-True (-not $unlockedLogin.forcePasswordChange) `
        "Unlocking must not enable self-service password changes."
    Invoke-Json -Method Put -Uri "$baseUrl/api/users/$($temporaryUser.id)" -Headers $adminHeaders -Body @{
        fullName = "Temporary Superintendent"; email = "temporary@fsm.local"; role = 1
        isActive = $false; password = $null; vesselIds = @(); forcePasswordChange = $true
    } | Out-Null
    $disabledStatus = try {
        Invoke-Json -Method Post -Uri "$baseUrl/api/auth/login" -Body @{
            email = "temporary@fsm.local"; password = "Reset123!"
        } | Out-Null; 200
    } catch { [int]$_.Exception.Response.StatusCode }
    Assert-True ([int]$disabledStatus -eq 401) "Disabled user must not sign in."

    $recoveryStatus = try {
        Invoke-Json -Method Post -Uri "$baseUrl/api/auth/administrator-recovery/request" -Body @{
            email = "admin@fsm.local"
        } | Out-Null; 200
    } catch { [int]$_.Exception.Response.StatusCode }
    Assert-True ([int]$recoveryStatus -eq 503) `
        "Email recovery must clearly require SMTP configuration."

    $balticVessel = Invoke-Json `
        -Method Post `
        -Uri "$baseUrl/api/vessels" `
        -Headers $adminHeaders `
        -Body @{
            vesselName = "MV BALTIC"
            imoNumber = "9126998"
            flag = "Cyprus"
            companyName = "FSM Test"
            invoicingDetails = $null
            accountingEmail = "baltic@fsm.local"
        }

    $balticVesselId = [string]$balticVessel.id

    $secondVessel = Invoke-Json `
        -Method Post `
        -Uri "$baseUrl/api/vessels" `
        -Headers $adminHeaders `
        -Body @{
            vesselName = "MV AMADEUS"
            imoNumber = "9000001"
            flag = "Lithuania"
            companyName = "FSM Test"
            invoicingDetails = $null
            accountingEmail = "test@fsm.local"
        }

    $secondVesselId = [string]$secondVessel.id

    Assert-True (-not [string]::IsNullOrWhiteSpace($secondVesselId)) `
        "Vessel creation must return the persisted vessel ID."

    Invoke-Json `
        -Method Put `
        -Uri "$baseUrl/api/users/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" `
        -Headers $adminHeaders `
        -Body @{
            fullName = "Technical Superintendent"
            email = "superintendent@fsm.local"
            role = 1
            isActive = $true
            password = $null
            vesselIds = @($balticVesselId, $secondVesselId)
        } | Out-Null

    Invoke-Json `
        -Method Put `
        -Uri "$baseUrl/api/users/cccccccc-cccc-cccc-cccc-cccccccccccc" `
        -Headers $adminHeaders `
        -Body @{
            fullName = "Vessel Crew"
            email = "crew@fsm.local"
            role = 2
            isActive = $true
            password = $null
            vesselIds = @($balticVesselId, $secondVesselId)
        } | Out-Null

    $balticEquipment = Invoke-Json `
        -Method Post `
        -Uri "$baseUrl/api/vessels/$balticVesselId/equipment" `
        -Headers $adminHeaders `
        -Body @{
            category = 1
            equipmentName = "MV BALTIC Fire Extinguisher"
            maker = $null
            model = $null
            serialNumber = "BALTIC-001"
            quantity = 1
            inspectionBy = 0
            expiryDate = "2027-02-15"
            remarks = $null
        }

    $amadeusEquipment = Invoke-Json `
        -Method Post `
        -Uri "$baseUrl/api/vessels/$secondVesselId/equipment" `
        -Headers $adminHeaders `
        -Body @{
            category = 0
            equipmentName = "Role Visibility Test EPIRB"
            maker = $null
            model = $null
            serialNumber = "AUTH-001"
            quantity = 1
            inspectionBy = 0
            expiryDate = "2027-01-15"
            remarks = $null
        }

    $eebdEquipment = Invoke-Json `
        -Method Post `
        -Uri "$baseUrl/api/vessels/$balticVesselId/equipment" `
        -Headers $adminHeaders `
        -Body @{
            category = 1
            equipmentName = "EEBD"
            maker = "Drager"
            model = "Oxy 3000"
            serialNumber = "EEBD-001"
            quantity = 1
            inspectionBy = 1
            expiryDate = "2026-08-04"
            remarks = "Clear expiry test"
        }

    $updatedEpirb = Invoke-Json `
        -Method Put `
        -Uri "$baseUrl/api/vessels/$secondVesselId/equipment/$($amadeusEquipment.id)" `
        -Headers $adminHeaders `
        -Body @{
            category = 0
            equipmentName = "EPIRB"
            maker = "Jotron"
            model = "Tron 60S"
            serialNumber = "AUTH-001"
            quantity = 2
            inspectionBy = 0
            expiryDate = "2026-01-01"
            remarks = "Edited expiry"
        }
    Assert-True ($updatedEpirb.status -eq "Overdue") `
        "Editing EPIRB expiry must recalculate status immediately."

    $updatedEebd = Invoke-Json `
        -Method Put `
        -Uri "$baseUrl/api/vessels/$balticVesselId/equipment/$($eebdEquipment.id)" `
        -Headers $adminHeaders `
        -Body @{
            category = 1
            equipmentName = "EEBD"
            maker = "Drager"
            model = "Oxy 3000"
            serialNumber = "EEBD-001"
            quantity = 1
            inspectionBy = 1
            expiryDate = $null
            remarks = "No expiry applicable"
        }
    Assert-True ($null -eq $updatedEebd.expiryDate -and $updatedEebd.status -eq "OK") `
        "Cleared EEBD expiry must remain empty and valid."

    foreach ($vesselCase in @(
        @{
            VesselId = $balticVesselId
            EquipmentId = [string]$balticEquipment.id
        },
        @{
            VesselId = $secondVesselId
            EquipmentId = [string]$amadeusEquipment.id
        }
    )) {
        $serviceRequest = Invoke-FileDownload `
            -Uri "$baseUrl/api/service-request" `
            -Headers $adminHeaders `
            -Body @{
                vesselId = $vesselCase.VesselId
                equipmentIds = @($vesselCase.EquipmentId)
                port = $null
                eta = $null
                etd = $null
                remarks = $null
                includeCertificates = $false
                includeReports = $false
            }
        $inspectionList = Invoke-FileDownload `
            -Uri "$baseUrl/api/inspection-list" `
            -Headers $adminHeaders `
            -Body @{
                vesselId = $vesselCase.VesselId
                equipmentIds = @($vesselCase.EquipmentId)
                format = "excel"
                includeCertificates = $false
            }

        Assert-True ($serviceRequest.StatusCode -eq 200) `
            "Service Requests must resolve every authoritative vessel."
        Assert-True ($inspectionList.StatusCode -eq 200) `
            "Inspection Lists must resolve every authoritative vessel."
    }

    $adminVessels = @((
        Invoke-Json -Method Get -Uri "$baseUrl/api/vessels" -Headers $adminHeaders) |
            ForEach-Object { $_ })
    $adminDashboard = Invoke-Json `
        -Method Get `
        -Uri "$baseUrl/api/dashboard" `
        -Headers $adminHeaders
    $adminSynchronization = Invoke-Json `
        -Method Get `
        -Uri "$baseUrl/api/synchronization/status" `
        -Headers $adminHeaders
    $expectedVesselNames = "MV AMADEUS|MV BALTIC"
    $vesselApiNames = @($adminVessels |
        ForEach-Object { $_.vesselName } |
        Sort-Object) -join "|"
    $fleetStatusNames = @($adminDashboard.vesselRows |
        ForEach-Object { $_.vesselName } |
        Sort-Object) -join "|"
    $synchronizationNames = @($adminSynchronization.vessels |
        ForEach-Object { $_.vesselName } |
        Sort-Object) -join "|"

    Assert-True ($adminVessels.Count -eq 2) `
        "System Administrator must receive every vessel."
    Assert-True ($adminDashboard.vessels -eq 2) `
        "System Administrator dashboard must count every vessel."
    Assert-True ($adminDashboard.equipment -eq 3) `
        "System Administrator dashboard must include fleet-wide equipment."
    Assert-True (@($adminDashboard.vesselRows).Count -eq 2) `
        "System Administrator Fleet Status must include every vessel."
    Assert-True (
        $vesselApiNames -eq $expectedVesselNames -and
        $fleetStatusNames -eq $expectedVesselNames -and
        $synchronizationNames -eq $expectedVesselNames) `
        "Vessels, Fleet Status, User Assignment and Synchronization must use the same vessel catalog."
    Assert-True (@($adminSynchronization.vessels).Count -eq 2) `
        "Synchronization status must include every authoritative vessel."
    Assert-True (
        @($adminDashboard.upcoming | Where-Object vesselId -eq $secondVesselId).Count -eq 1) `
        "System Administrator Upcoming Inspections must include the new vessel."
    $technicalSuperintendent = @($adminDashboard.technicalSuperintendents |
        Where-Object fullName -eq "Technical Superintendent")[0]
    Assert-True ($null -ne $technicalSuperintendent) `
        "Office dashboard must list every Technical Superintendent."
    Assert-True ($technicalSuperintendent.vesselCount -eq 2 -and
        @($technicalSuperintendent.vessels).Count -eq 2) `
        "Technical Superintendent vessel counts and assigned vessel details must match."
    Assert-True ($technicalSuperintendent.soon -eq 0 -and
        $technicalSuperintendent.overdue -eq 1) `
        "Technical Superintendent Soon and Overdue counts must use assigned equipment only."

    Stop-FsmApi $process
    $process = $null

    $officePort = Get-FreeTcpPort
    $office = Start-FsmApi `
        -Edition Office `
        -ShipVesselId "" `
        -Port $officePort
    $process = $office[0]
    $baseUrl = $office[1]
    $adminHeaders = Get-AuthorizationHeader `
        -BaseUrl $baseUrl `
        -Email "admin@fsm.local" `
        -Password $adminPassword
    $persistedAdminVessels = @((
        Invoke-Json -Method Get -Uri "$baseUrl/api/vessels" -Headers $adminHeaders) |
            ForEach-Object { $_ })
    $persistedAdminDashboard = Invoke-Json `
        -Method Get `
        -Uri "$baseUrl/api/dashboard" `
        -Headers $adminHeaders

    Assert-True ($persistedAdminVessels.Count -eq 2) `
        "Both vessels must remain visible after an Office API restart."
    Assert-True ($persistedAdminDashboard.vessels -eq 2) `
        "The dashboard vessel count must remain two after an Office API restart."
    Assert-True (@($persistedAdminDashboard.vesselRows).Count -eq 2) `
        "Fleet Status must retain both vessels after an Office API restart."
    $persistedAmadeusEquipment = @((
        Invoke-Json -Method Get `
            -Uri "$baseUrl/api/vessels/$secondVesselId/equipment" `
            -Headers $adminHeaders) | ForEach-Object { $_ })
    $persistedBalticEquipment = @((
        Invoke-Json -Method Get `
            -Uri "$baseUrl/api/vessels/$balticVesselId/equipment" `
            -Headers $adminHeaders) | ForEach-Object { $_ })
    $persistedEpirb = $persistedAmadeusEquipment |
        Where-Object id -eq $amadeusEquipment.id
    $persistedEebd = $persistedBalticEquipment |
        Where-Object id -eq $eebdEquipment.id
    Assert-True ($persistedEpirb.expiryDate -eq "2026-01-01" -and
        $persistedEpirb.status -eq "Overdue") `
        "EPIRB edit and recalculated status must persist after restart."
    Assert-True ($null -eq $persistedEebd.expiryDate -and
        $persistedEebd.status -eq "OK") `
        "Cleared EEBD expiry must persist after restart."
    Assert-True (
        (Get-StatusCode `
            -Uri "$baseUrl/api/vessels/$balticVesselId/equipment" `
            -Headers $adminHeaders) -eq 200 -and
        (Get-StatusCode `
            -Uri "$baseUrl/api/vessels/$secondVesselId/equipment" `
            -Headers $adminHeaders) -eq 200) `
        "System Administrator must be able to open both persisted vessels."

    $superintendentHeaders = Get-AuthorizationHeader `
        -BaseUrl $baseUrl `
        -Email "superintendent@fsm.local" `
        -Password "Super123!"
    $bothAssignedVessels = @((
        Invoke-Json -Method Get -Uri "$baseUrl/api/vessels" -Headers $superintendentHeaders) |
            ForEach-Object { $_ })
    Assert-True ($bothAssignedVessels.Count -eq 2) `
        "Superintendent assigned both vessels must see both vessels."

    Invoke-Json `
        -Method Put `
        -Uri "$baseUrl/api/users/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" `
        -Headers $adminHeaders `
        -Body @{
            fullName = "Technical Superintendent"
            email = "superintendent@fsm.local"
            role = 1
            isActive = $true
            password = $null
            vesselIds = @($secondVesselId)
        } | Out-Null
    $superintendentVessels = @((
        Invoke-Json `
            -Method Get `
            -Uri "$baseUrl/api/vessels" `
            -Headers $superintendentHeaders) | ForEach-Object { $_ })
    $superintendentDashboard = Invoke-Json `
        -Method Get `
        -Uri "$baseUrl/api/dashboard" `
        -Headers $superintendentHeaders

    Assert-True (
        $superintendentVessels.Count -eq 1 -and
        $superintendentVessels[0].id -eq $secondVesselId) `
        "Superintendent must receive only assigned vessels."
    Assert-True (
        $superintendentDashboard.vessels -eq 1 -and
        $superintendentDashboard.equipment -eq 1) `
        "Superintendent dashboard totals must use assigned vessels only."
    Assert-True (
        @($superintendentDashboard.vesselRows | Where-Object id -ne $secondVesselId).Count -eq 0) `
        "Superintendent Fleet Status exposed an unauthorized vessel."
    Assert-True (
        @($superintendentDashboard.upcoming | Where-Object vesselId -ne $secondVesselId).Count -eq 0) `
        "Superintendent Upcoming Inspections exposed an unauthorized vessel."
    Assert-True (@($superintendentDashboard.upcoming).Count -eq 1) `
        "Superintendent dashboard must retain Upcoming Inspections."
    Assert-True (@($superintendentDashboard.technicalSuperintendents).Count -eq 0) `
        "Technical Superintendents must be an Office Administrator dashboard feature only."
    Assert-True (
        (Get-StatusCode `
            -Uri "$baseUrl/api/vessels/$balticVesselId/equipment" `
            -Headers $superintendentHeaders) -eq 403) `
        "An unauthorized vessel ID must return HTTP 403 for Superintendent."

    Stop-FsmApi $process
    $process = $null

    $shipPort = Get-FreeTcpPort
    $ship = Start-FsmApi `
        -Edition Ship `
        -ShipVesselId $secondVesselId `
        -Port $shipPort
    $process = $ship[0]
    $baseUrl = $ship[1]
    $crewHeaders = Get-AuthorizationHeader `
        -BaseUrl $baseUrl `
        -Email "crew@fsm.local" `
        -Password "Crew123!"
    $crewVessels = @((
        Invoke-Json -Method Get -Uri "$baseUrl/api/vessels" -Headers $crewHeaders) |
            ForEach-Object { $_ })
    $crewDashboard = Invoke-Json `
        -Method Get `
        -Uri "$baseUrl/api/dashboard" `
        -Headers $crewHeaders

    Assert-True (
        $crewVessels.Count -eq 1 -and
        $crewVessels[0].id -eq $secondVesselId) `
        "Ship Crew must receive exactly the configured local vessel."
    Assert-True (
        $crewDashboard.vessels -eq 1 -and
        $crewDashboard.equipment -eq 1) `
        "Ship Crew dashboard totals must use the local vessel only."
    Assert-True (
        @($crewDashboard.vesselRows | Where-Object id -ne $secondVesselId).Count -eq 0) `
        "Ship Crew Fleet Status exposed a non-local vessel."
    Assert-True (
        @($crewDashboard.upcoming | Where-Object vesselId -ne $secondVesselId).Count -eq 0) `
        "Ship Crew Upcoming Inspections exposed a non-local vessel."
    Assert-True (@($crewDashboard.upcoming).Count -eq 1) `
        "Ship dashboard must retain Upcoming Inspections."
    Assert-True (@($crewDashboard.technicalSuperintendents).Count -eq 0) `
        "Technical Superintendents must not be returned to the Ship dashboard."
    Assert-True (
        (Get-StatusCode `
            -Uri "$baseUrl/api/vessels/$balticVesselId/equipment" `
            -Headers $crewHeaders) -eq 403) `
        "A non-local vessel ID must return HTTP 403 for Ship Crew."

    Stop-FsmApi $process
    $process = $null
    $databaseText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($databasePath))
    $normalLogs = ((Get-Content $logPath -Raw -ErrorAction SilentlyContinue) +
        (Get-Content $errorLogPath -Raw -ErrorAction SilentlyContinue))
    foreach ($secret in @("Temporary123!", "Changed123!", "Reset123!", "Recovered123!")) {
        Assert-True (-not $databaseText.Contains($secret)) "Plaintext password leaked into SQLite."
        Assert-True (-not $normalLogs.Contains($secret)) "Plaintext password leaked into normal logs."
    }
    Assert-True (-not ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($newKey))).Contains($adminPassword)) `
        "Recovery file must not contain a readable password."

    $dashboardSource = Get-Content `
        -LiteralPath (Join-Path $root "frontend\src\main.tsx") `
        -Raw
    Assert-True ($dashboardSource.Contains('<h2>Fleet Status</h2>')) `
        "Fleet Inspection Status panel must remain present."
    Assert-True ($dashboardSource.Contains('<h2>Technical Superintendents</h2>')) `
        "Office dashboard must render the Technical Superintendents panel."
    Assert-True ($dashboardSource.Contains('setSelectedSuperintendentId(current =>')) `
        "Clicking a Technical Superintendent must reveal that Superintendent's vessels."
    Assert-True ($dashboardSource.Contains('if (vessel) setCurrentVessel(vessel);')) `
        "Clicking an assigned vessel must open the existing Equipment Register."
    Assert-True ($dashboardSource -match '(?s)\)\s*:\s*\(\s*<section className="dashboard-section">.*?<h2>Upcoming Inspections</h2>') `
        "Non-Office dashboards must retain the existing Upcoming Inspections panel."
    Assert-True ($dashboardSource.Contains('const showUpcomingVesselColumn = user?.role === "OfficeAdmin";')) `
        "Upcoming Inspections must show the Vessel column for Office Administrator only."
    Assert-True (([regex]::Matches(
        $dashboardSource,
        'showUpcomingVesselColumn &&')).Count -eq 2) `
        "The Upcoming Inspections Vessel header and row cell must use the same role rule."
    Assert-True ($dashboardSource.Contains('<th>Days Left</th>') -and
        $dashboardSource.Contains('<td>{item.daysLeft}</td>')) `
        "Days Left must remain visible after the Vessel column is removed."
    Assert-True ($dashboardSource.Contains('.filter(item => dashboardFilter === "All" || item.status === dashboardFilter)')) `
        "Upcoming Inspections status filtering must remain unchanged."
    Assert-True ($dashboardSource.Contains('className={`status ${item.status.toLowerCase()}`}')) `
        "Upcoming Inspections status values and colours must remain unchanged."
    Assert-True ($dashboardSource.Contains('const vessel = vessels.find(value => value.id === item.vesselId);')) `
        "Clicking Upcoming Inspections equipment must continue opening its Equipment Register."

    Write-Host "Role visibility integration tests passed."
    Write-Host "  System Administrator: all vessels and fleet totals"
    Write-Host "  Vessel source: MV BALTIC and MV AMADEUS match across modules"
    Write-Host "  Equipment, Service Requests and Inspection Lists: both vessels"
    Write-Host "  Vessel creation: persisted and visible after API restart"
    Write-Host "  Superintendent: assigned vessel only"
    Write-Host "  Ship Crew: configured local vessel only"
    Write-Host "  Unauthorized vessel IDs: HTTP 403"
}
finally {
    Stop-FsmApi $process
    if (Test-Path $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
