$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$apiProject = Join-Path $root "backend\src\FSM.Api\FSM.Api.csproj"
$apiDll = Join-Path $root "backend\src\FSM.Api\bin\Release\net9.0\FSM.Api.dll"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("fsm-equipment-edit-" + [Guid]::NewGuid().ToString("N"))
$databasePath = Join-Path $testRoot "fsm.db"
$process = $null

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
        $arguments.Body = $Body | ConvertTo-Json -Depth 12
    }
    $response = Invoke-WebRequest @arguments
    if ([string]::IsNullOrWhiteSpace($response.Content)) { return $null }
    return $response.Content | ConvertFrom-Json
}

function Start-Api {
    $port = Get-FreeTcpPort
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command dotnet).Source
    $startInfo.Arguments = "`"$apiDll`" --urls http://127.0.0.1:$port"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables["ConnectionStrings__DefaultConnection"] = "Data Source=$databasePath"
    $startInfo.EnvironmentVariables["Storage__RootPath"] = (Join-Path $testRoot "Documents")
    $startInfo.EnvironmentVariables["Application__Edition"] = "Office"
    $startInfo.EnvironmentVariables["Notifications__Enabled"] = "false"
    $started = [Diagnostics.Process]::new()
    $started.StartInfo = $startInfo
    if (-not $started.Start()) { throw "Unable to start API." }
    $baseUrl = "http://127.0.0.1:$port"
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            Invoke-WebRequest -Uri "$baseUrl/swagger/v1/swagger.json" -UseBasicParsing | Out-Null
            return @($started, $baseUrl)
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    throw "API did not become ready."
}

function Stop-Api([Diagnostics.Process]$ApiProcess) {
    if ($null -ne $ApiProcess -and -not $ApiProcess.HasExited) {
        $ApiProcess.Kill()
        $ApiProcess.WaitForExit()
    }
    if ($null -ne $ApiProcess) { $ApiProcess.Dispose() }
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    dotnet build $apiProject -c Release
    if ($LASTEXITCODE -ne 0) { throw "Backend build failed." }

    $started = Start-Api
    $process = $started[0]
    $baseUrl = $started[1]
    $login = Invoke-Json Post "$baseUrl/api/auth/login" @{} @{
        email = "admin@fsm.local"; password = "Admin123!"
    }
    $headers = @{ Authorization = "Bearer $($login.token)" }
    $vessel = Invoke-Json Post "$baseUrl/api/vessels" $headers @{
        vesselName = "MV EDIT TEST"; imoNumber = "9008008"; flag = "Lithuania"
    }
    $futureDate = [DateTime]::UtcNow.Date.AddDays(120).ToString("yyyy-MM-dd")
    $overdueDate = [DateTime]::UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd")
    $epirb = Invoke-Json Post "$baseUrl/api/vessels/$($vessel.id)/equipment" $headers @{
        category = 0; equipmentName = "EPIRB"; serialNumber = "RC20-008"
        quantity = 1; inspectionBy = 0; expiryDate = $futureDate
        responsible = "Chief Officer"; remarks = "Initial"
    }
    Assert-True ($epirb.status -eq "OK") "Future EPIRB must initially be OK."

    $saved = Invoke-Json Put "$baseUrl/api/vessels/$($vessel.id)/equipment/$($epirb.id)" $headers @{
        category = 0; equipmentName = "EPIRB"; serialNumber = "RC20-008"
        quantity = 2; inspectionBy = 1; expiryDate = $overdueDate
        responsible = "Master"; remarks = "Changed"
    }
    Assert-True ($saved.expiryDate -eq $overdueDate) "Updated expiry date must be returned."
    Assert-True ($saved.status -eq "Overdue") "Updated EPIRB status must be recalculated."
    Assert-True ($saved.responsible -eq "Master") "Responsible must be persisted."

    $dashboard = Invoke-Json Get "$baseUrl/api/dashboard" $headers
    Assert-True ($dashboard.overdue -eq 1 -and $dashboard.ok -eq 0) "Dashboard counters must update."
    Assert-True ($dashboard.vesselRows[0].overdue -eq 1) "Fleet Status must update."
    Assert-True ($dashboard.upcoming[0].id -eq $epirb.id) "Upcoming Inspections must update."

    try {
        Invoke-Json Put "$baseUrl/api/vessels/$($vessel.id)/equipment/$($epirb.id)" $headers @{
            category = 0; equipmentName = "EPIRB"; quantity = 2
            inspectionBy = 1; expiryDate = "2026-02-30"
        } | Out-Null
        throw "Invalid date was accepted."
    }
    catch {
        $errorBody = $_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($errorBody) -and $_.Exception.Response) {
            $reader = [IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
            try { $errorBody = $reader.ReadToEnd() }
            finally { $reader.Dispose() }
        }
        Assert-True ($errorBody -like '*valid Expiry Date*') "Invalid date must return a friendly error."
    }

    $syncToken = (Invoke-Json Post "$baseUrl/api/synchronization/token/$($vessel.id)" $headers).token
    $syncHeaders = @{
        "X-FSM-Synchronization-Token" = $syncToken
        "X-Forwarded-Proto" = "https"
    }
    $exchange = Invoke-Json Post "$baseUrl/api/synchronization/exchange" $syncHeaders @{
        applicationVersion = "0.20.0-RC20"; sourceEdition = "Ship"; vesselId = $vessel.id
        remoteCursorUtc = "1970-01-01T00:00:00Z"
        changes = @{ vessels = @(); equipment = @(); users = @(); userVessels = @(); vesselSettings = @(); files = @() }
    }
    $transferred = @($exchange.changes.equipment | Where-Object id -eq $epirb.id)[0]
    Assert-True ($transferred.expiryDate -eq $overdueDate) "Synchronization must transfer the new expiry date."

    Stop-Api $process
    $process = $null
    $started = Start-Api
    $process = $started[0]
    $baseUrl = $started[1]
    $login = Invoke-Json Post "$baseUrl/api/auth/login" @{} @{
        email = "admin@fsm.local"; password = "Admin123!"
    }
    $headers = @{ Authorization = "Bearer $($login.token)" }
    $persisted = @(Invoke-Json Get "$baseUrl/api/vessels/$($vessel.id)/equipment" $headers)[0]
    Assert-True ($persisted.expiryDate -eq $overdueDate) "Expiry date must persist after restart."
    Assert-True ($persisted.status -eq "Overdue") "Status must remain correct after restart."

    Write-Host "Equipment editing integration tests passed."
}
finally {
    Stop-Api $process
    if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force }
}
