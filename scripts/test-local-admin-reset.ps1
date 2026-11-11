$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $root "publish\FSM.Api.exe"
if (-not (Test-Path $executable)) {
    throw "Published FSM.Api.exe is missing."
}

$testRoot = Join-Path $env:RUNNER_TEMP "fsm-local-admin-reset"
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$database = Join-Path $testRoot "fsm-reset-test.db"
$keys = Join-Path $testRoot "keys"
$server = $null

try {
    $env:ConnectionStrings__DefaultConnection = "Data Source=$database"
    $env:DataProtection__KeyPath = $keys
    $env:Application__Edition = "Office"

    @("NewAdmin123!", "NewAdmin123!") |
        & $executable --reset-administrator-password
    if ($LASTEXITCODE -ne 0) {
        throw "Local administrator password reset failed with exit code $LASTEXITCODE."
    }

    $server = Start-Process -FilePath $executable `
        -ArgumentList "--urls", "http://127.0.0.1:18088" `
        -WorkingDirectory (Split-Path -Parent $executable) `
        -PassThru -WindowStyle Hidden

    $loginUrl = "http://127.0.0.1:18088/api/auth/login"
    $deadline = (Get-Date).AddSeconds(40)
    do {
        try {
            $login = Invoke-RestMethod -Method Post -Uri $loginUrl `
                -ContentType "application/json" `
                -Body (@{
                    username = "admin@fsm.local"
                    password = "NewAdmin123!"
                } | ConvertTo-Json)
            break
        }
        catch {
            if ((Get-Date) -ge $deadline) { throw }
            Start-Sleep -Milliseconds 500
        }
    } while ($true)

    if ($login.role -ne "OfficeAdmin") {
        throw "The reset password did not authenticate the Office Administrator."
    }
    Write-Host "Local Office Administrator password reset verified."
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
        $server.WaitForExit(10000) | Out-Null
    }
    Remove-Item Env:ConnectionStrings__DefaultConnection -ErrorAction SilentlyContinue
    Remove-Item Env:DataProtection__KeyPath -ErrorAction SilentlyContinue
    Remove-Item Env:Application__Edition -ErrorAction SilentlyContinue
    if (Test-Path $testRoot) {
        Remove-Item $testRoot -Recurse -Force
    }
}
