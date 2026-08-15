$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$apiProject = Join-Path $root "backend\src\FSM.Api\FSM.Api.csproj"
$apiDll = Join-Path $root "backend\src\FSM.Api\bin\Release\net9.0\FSM.Api.dll"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("fsm-rc20-021-" + [Guid]::NewGuid().ToString("N"))
$databasePath = Join-Path $testRoot "Data\fsm.db"
$process = $null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); try { ([Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}
function Invoke-Json([string]$Method, [string]$Uri, [hashtable]$Headers = @{}, $Body = $null) {
    $arguments = @{ Method = $Method; Uri = $Uri; Headers = $Headers; UseBasicParsing = $true }
    if ($null -ne $Body) { $arguments.ContentType = "application/json"; $arguments.Body = $Body | ConvertTo-Json -Depth 8 }
    $response = Invoke-WebRequest @arguments
    if ([string]::IsNullOrWhiteSpace($response.Content)) { return $null }
    $response.Content | ConvertFrom-Json
}
function Start-TestApi {
    $port = Get-FreePort
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = (Get-Command dotnet).Source
    $info.Arguments = "`"$apiDll`" --urls http://127.0.0.1:$port"
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.EnvironmentVariables["ConnectionStrings__DefaultConnection"] = "Data Source=$databasePath"
    $info.EnvironmentVariables["Storage__RootPath"] = (Join-Path $testRoot "Documents")
    $info.EnvironmentVariables["Application__Edition"] = "Office"
    $info.EnvironmentVariables["Notifications__Enabled"] = "false"
    $started = [Diagnostics.Process]::new(); $started.StartInfo = $info
    if (-not $started.Start()) { throw "API failed to start." }
    $baseUrl = "http://127.0.0.1:$port"
    for ($i = 0; $i -lt 50; $i++) {
        try { Invoke-WebRequest "$baseUrl/swagger/v1/swagger.json" -UseBasicParsing | Out-Null; return @($started, $baseUrl) }
        catch { Start-Sleep -Milliseconds 200 }
    }
    throw "API did not become ready. $($started.StandardError.ReadToEnd())"
}
function Stop-TestApi($item) {
    if ($null -ne $item -and -not $item.HasExited) { $item.Kill(); $item.WaitForExit() }
    if ($null -ne $item) { $item.Dispose() }
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    Assert-True (-not (Test-Path $databasePath)) "Test must begin without a SQLite database."
    dotnet build $apiProject -c Release
    if ($LASTEXITCODE -ne 0) { throw "Release build failed." }

    $started = Start-TestApi; $process = $started[0]; $baseUrl = $started[1]
    Assert-True ((Test-Path $databasePath) -and (Get-Item $databasePath).Length -gt 0) "Fresh SQLite database was not created at the configured path."
    Stop-TestApi $process; $process = $null
    python (Join-Path $PSScriptRoot "prepare_rc20_022_legacy_db.py") $databasePath
    if ($LASTEXITCODE -ne 0) { throw "Unable to prepare the legacy Email-identity database fixture." }
    $started = Start-TestApi; $process = $started[0]; $baseUrl = $started[1]
    $login = Invoke-Json Post "$baseUrl/api/auth/login" @{} @{ email = "admin@fsm.local"; password = "Admin123!" }
    $headers = @{ Authorization = "Bearer $($login.token)" }
    Assert-True ($login.username -eq "admin@fsm.local") "Existing user Username was not migrated from Email."
    $firstUser = Invoke-Json Post "$baseUrl/api/users" $headers @{
        fullName = "Duplicate Email One"; username = "duplicate-one"
        email = "shared@fsm.local"; role = 1; isActive = $true
        password = "Duplicate123!"; vesselIds = @(); forcePasswordChange = $false
    }
    $secondUser = Invoke-Json Post "$baseUrl/api/users" $headers @{
        fullName = "Duplicate Email Two"; username = "duplicate-two"
        email = "shared@fsm.local"; role = 1; isActive = $true
        password = "Duplicate123!"; vesselIds = @(); forcePasswordChange = $false
    }
    $noEmailUser = Invoke-Json Post "$baseUrl/api/users" $headers @{
        fullName = "No Email User"; username = "no-email-user"
        email = $null; role = 1; isActive = $true
        password = "Duplicate123!"; vesselIds = @(); forcePasswordChange = $false
    }
    Assert-True ($firstUser.username -eq "duplicate-one" -and $secondUser.username -eq "duplicate-two") "Unique usernames were not persisted."
    Assert-True ($firstUser.email -eq $secondUser.email) "Duplicate optional email addresses must be accepted."
    Assert-True ($null -eq $noEmailUser.email) "Email must be optional."
    $usernameLogin = Invoke-Json Post "$baseUrl/api/auth/login" @{} @{ username = "duplicate-one"; password = "Duplicate123!" }
    Assert-True ($usernameLogin.username -eq "duplicate-one") "Authentication did not use Username."
    Invoke-Json Post "$baseUrl/api/users/$($firstUser.id)/reset-password" $headers @{
        password = "ResetDuplicate123!"; temporaryPassword = $false
    } | Out-Null
    $auditEntries = @(Invoke-Json Get "$baseUrl/api/audit" $headers)
    $passwordResetAudit = @($auditEntries | Where-Object {
        $_.action -eq "Password reset" -and $_.targetUser -eq "duplicate-one"
    })
    Assert-True ($passwordResetAudit.Count -gt 0) "Password reset audit target must use Username."
    $created = Invoke-Json Post "$baseUrl/api/vessels" $headers @{ vesselName = "MV FRESH DATABASE"; imoNumber = "9123456"; flag = "Lithuania" }
    Assert-True (-not [string]::IsNullOrWhiteSpace($created.id)) "POST did not return the persisted vessel ID."

    $vessels = @(Invoke-Json Get "$baseUrl/api/vessels" $headers)
    $dashboard = Invoke-Json Get "$baseUrl/api/dashboard" $headers
    Assert-True ($vessels.Count -eq 1 -and $vessels[0].id -eq $created.id) "GET did not return the inserted vessel."
    Assert-True ($dashboard.vessels -eq 1) "Dashboard vessel count must equal one."
    Assert-True (@($dashboard.vesselRows).Count -eq 1 -and $dashboard.vesselRows[0].id -eq $created.id) "Fleet Status does not contain the inserted vessel."
    Assert-True ($dashboard.equipment -eq 0 -and @($dashboard.upcoming).Count -eq 0) "Fresh-vessel equipment totals are inconsistent."

    Stop-TestApi $process; $process = $null
    $started = Start-TestApi; $process = $started[0]; $baseUrl = $started[1]
    $login = Invoke-Json Post "$baseUrl/api/auth/login" @{} @{ email = "admin@fsm.local"; password = "Admin123!" }
    $headers = @{ Authorization = "Bearer $($login.token)" }
    $persisted = @(Invoke-Json Get "$baseUrl/api/vessels" $headers)
    $persistedDashboard = Invoke-Json Get "$baseUrl/api/dashboard" $headers
    Assert-True ($persisted.Count -eq 1 -and $persisted[0].id -eq $created.id) "Vessel did not persist after backend restart."
    Assert-True ($persistedDashboard.vessels -eq 1 -and @($persistedDashboard.vesselRows).Count -eq 1) "Dashboard/Fleet Status lost the vessel after restart."
    Write-Host "RC20-021/022 fresh SQLite and Username migration workflows passed."
}
finally {
    Stop-TestApi $process
    if (Test-Path $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
