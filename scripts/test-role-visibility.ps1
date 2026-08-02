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
$seedVesselId = "11111111-1111-1111-1111-111111111111"
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
        -Password "Admin123!"

    $secondVessel = Invoke-Json `
        -Method Post `
        -Uri "$baseUrl/api/vessels" `
        -Headers $adminHeaders `
        -Body @{
            vesselName = "MV AUTHORIZED"
            imoNumber = "9000001"
            flag = "Lithuania"
            companyName = "FSM Test"
            invoicingDetails = $null
            accountingEmail = "test@fsm.local"
        }

    $secondVesselId = [string]$secondVessel.id

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
            vesselIds = @($seedVesselId, $secondVesselId)
        } | Out-Null

    Invoke-Json `
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
        } | Out-Null

    $adminVessels = @((
        Invoke-Json -Method Get -Uri "$baseUrl/api/vessels" -Headers $adminHeaders) |
            ForEach-Object { $_ })
    $adminDashboard = Invoke-Json `
        -Method Get `
        -Uri "$baseUrl/api/dashboard" `
        -Headers $adminHeaders

    Assert-True ($adminVessels.Count -eq 2) `
        "System Administrator must receive every vessel."
    Assert-True ($adminDashboard.vessels -eq 2) `
        "System Administrator dashboard must count every vessel."
    Assert-True ($adminDashboard.equipment -eq 7) `
        "System Administrator dashboard must include fleet-wide equipment."
    Assert-True (@($adminDashboard.vesselRows).Count -eq 2) `
        "System Administrator Fleet Status must include every vessel."
    Assert-True (
        @($adminDashboard.upcoming | Where-Object vesselId -eq $secondVesselId).Count -eq 1) `
        "System Administrator Upcoming Inspections must include the new vessel."

    $superintendentHeaders = Get-AuthorizationHeader `
        -BaseUrl $baseUrl `
        -Email "superintendent@fsm.local" `
        -Password "Super123!"
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
    Assert-True (
        (Get-StatusCode `
            -Uri "$baseUrl/api/vessels/$seedVesselId/equipment" `
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
    Assert-True (
        (Get-StatusCode `
            -Uri "$baseUrl/api/vessels/$seedVesselId/equipment" `
            -Headers $crewHeaders) -eq 403) `
        "A non-local vessel ID must return HTTP 403 for Ship Crew."

    Write-Host "Role visibility integration tests passed."
    Write-Host "  System Administrator: all vessels and fleet totals"
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
