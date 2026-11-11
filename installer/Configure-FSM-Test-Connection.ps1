[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9-]+$')]
    [string]$MachineName = 'fsm-office-test',

    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-ForEnter {
    param([string]$Message = 'Press Enter to close this window')
    if (-not $NoPause) {
        [void](Read-Host $Message)
    }
}

function Test-FsmSynchronizationHealth {
    param([Parameter(Mandatory = $true)][string]$Uri)
    try {
        $health = Invoke-RestMethod -Uri $Uri -TimeoutSec 10
        return ($health.applicationEdition -eq 'Office' -and
                $health.databaseAvailable -eq $true -and
                $health.synchronizationAvailable -eq $true)
    } catch {
        return $false
    }
}

function Invoke-TailscaleCommand {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [ValidateRange(5, 300)][int]$TimeoutSeconds = 30
    )

    $stdoutPath = Join-Path $env:TEMP ("fsm-tailscale-{0}.out" -f [guid]::NewGuid())
    $stderrPath = Join-Path $env:TEMP ("fsm-tailscale-{0}.err" -f [guid]::NewGuid())
    try {
        $process = Start-Process `
            -FilePath $tailscalePath `
            -ArgumentList $Arguments `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru
        $completed = $process.WaitForExit($TimeoutSeconds * 1000)
        if (-not $completed) {
            try { $process.Kill() } catch { }
            try { $process.WaitForExit() } catch { }
        }
        $stdout = if (Test-Path $stdoutPath) { Get-Content $stdoutPath -Raw } else { '' }
        $stderr = if (Test-Path $stderrPath) { Get-Content $stderrPath -Raw } else { '' }
        return [pscustomobject]@{
            ExitCode = if ($completed) { $process.ExitCode } else { 124 }
            TimedOut = (-not $completed)
            Output = (($stdout, $stderr) -join [Environment]::NewLine).Trim()
        }
    } finally {
        Remove-Item $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-IsAdministrator)) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-MachineName', $MachineName
    )
    if ($NoPause) { $arguments += '-NoPause' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments
    exit 0
}

try {
    $appRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $currentFile = Join-Path $appRoot 'current.txt'
    if (-not (Test-Path $currentFile)) {
        throw 'Fleet Safety Manager current release was not found.'
    }

    $releaseValue = (Get-Content $currentFile -Raw).Trim()
    $releasePath = if ([System.IO.Path]::IsPathRooted($releaseValue)) {
        $releaseValue
    } else {
        Join-Path $appRoot $releaseValue
    }
    $configPath = Join-Path $releasePath 'appsettings.json'
    if (-not (Test-Path $configPath)) {
        throw 'Fleet Safety Manager configuration was not found.'
    }

    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    if ($config.Application.Edition -ne 'Office') {
        throw 'This command can only be run on an Office installation.'
    }

    Write-Host ''
    Write-Host 'Fleet Safety Manager - Test Connection' -ForegroundColor Cyan
    Write-Host 'Checking the Office application...'
    Set-Service -Name 'FleetSafetyManager' -StartupType Automatic
    Restart-Service -Name 'FleetSafetyManager' -Force
    $localHealthUri = 'http://127.0.0.1:8080/api/synchronization/health'
    $localDeadline = (Get-Date).AddSeconds(60)
    do {
        $localReady = Test-FsmSynchronizationHealth -Uri $localHealthUri
        if (-not $localReady) { Start-Sleep -Seconds 2 }
    } while (-not $localReady -and (Get-Date) -lt $localDeadline)
    if (-not $localReady) {
        throw 'The local Fleet Safety Manager synchronization service is not ready.'
    }

    $tailscaleCandidates = @(
        (Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Tailscale\tailscale.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }
    $tailscalePath = $tailscaleCandidates | Select-Object -First 1

    if (-not $tailscalePath) {
        Write-Host 'Installing the secure test connector...'
        $packagesPage = (Invoke-WebRequest -Uri 'https://pkgs.tailscale.com/stable/' -UseBasicParsing).Content
        $packageMatches = [regex]::Matches(
            $packagesPage,
            'tailscale-setup-(?<version>\d+\.\d+\.\d+)-amd64\.msi'
        )
        if ($packageMatches.Count -eq 0) {
            throw 'The current Tailscale Windows installer could not be located.'
        }

        $package = $packageMatches | ForEach-Object {
            [pscustomobject]@{
                Version = [version]$_.Groups['version'].Value
                Name = $_.Value
            }
        } | Sort-Object Version -Descending | Select-Object -First 1

        $downloadUrl = 'https://pkgs.tailscale.com/stable/{0}' -f $package.Name
        $temporaryMsi = Join-Path $env:TEMP $package.Name
        Invoke-WebRequest -Uri $downloadUrl -OutFile $temporaryMsi -UseBasicParsing

        try {
            $checksumText = (Invoke-WebRequest -Uri ($downloadUrl + '.sha256') -UseBasicParsing).Content
            $expectedChecksum = ($checksumText -split '\s+')[0].Trim().ToLowerInvariant()
            $actualChecksum = (Get-FileHash $temporaryMsi -Algorithm SHA256).Hash.ToLowerInvariant()
            if (-not $expectedChecksum -or $actualChecksum -ne $expectedChecksum) {
                throw 'The downloaded Tailscale installer checksum is invalid.'
            }

            $msiArguments = @(
                '/i', ('"{0}"' -f $temporaryMsi),
                '/qn', '/norestart',
                'TS_UNATTENDEDMODE=always'
            )
            $installer = Start-Process -FilePath 'msiexec.exe' -ArgumentList $msiArguments -Wait -PassThru
            if ($installer.ExitCode -ne 0) {
                throw "Tailscale installation failed with exit code $($installer.ExitCode)."
            }
        } finally {
            Remove-Item $temporaryMsi -Force -ErrorAction SilentlyContinue
        }

        $tailscalePath = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
        if (-not (Test-Path $tailscalePath)) {
            throw 'Tailscale was installed, but its command was not found.'
        }
    }

    function Get-TailscaleStatus {
        try {
            $statusResult = Invoke-TailscaleCommand -Arguments @('status', '--json')
            if ($statusResult.ExitCode -eq 0 -and $statusResult.Output) {
                return ($statusResult.Output | ConvertFrom-Json)
            }
        } catch {
            return $null
        }
        return $null
    }

    $status = Get-TailscaleStatus
    if (-not $status -or $status.BackendState -ne 'Running') {
        Write-Host 'A browser window will open. Sign in to Tailscale once to continue.' -ForegroundColor Yellow
        # Do not let the CLI wait indefinitely for interactive browser sign-in.
        # It prints the authentication URL before this short timeout expires.
        $loginResult = Invoke-TailscaleCommand `
            -Arguments @('up', '--timeout=15s') `
            -TimeoutSeconds 30
        $loginUrl = [regex]::Match($loginResult.Output, 'https://[^\s]+').Value.Trim()
        if ($loginUrl) {
            Start-Process $loginUrl
        } elseif ($loginResult.ExitCode -ne 0) {
            throw "Tailscale sign-in could not start. $($loginResult.Output)"
        }

        $loginDeadline = (Get-Date).AddMinutes(5)
        do {
            Start-Sleep -Seconds 3
            $status = Get-TailscaleStatus
        } while ((-not $status -or $status.BackendState -ne 'Running') -and
                 (Get-Date) -lt $loginDeadline)

        if (-not $status -or $status.BackendState -ne 'Running') {
            throw 'Tailscale sign-in was not completed within five minutes. Run this shortcut again.'
        }
    }

    $hostnameResult = Invoke-TailscaleCommand -Arguments @('set', "--hostname=$MachineName")
    if ($hostnameResult.ExitCode -ne 0) {
        throw "The stable test computer name could not be configured. $($hostnameResult.Output)"
    }

    Write-Host 'Enabling the permanent HTTPS test address...'
    $funnelResult = Invoke-TailscaleCommand `
        -Arguments @('funnel', '--bg', '--yes', 'http://127.0.0.1:8080') `
        -TimeoutSeconds 45
    if ($funnelResult.ExitCode -ne 0) {
        $approvalUrl = [regex]::Match($funnelResult.Output, 'https://[^\s]+').Value.Trim()
        if ($approvalUrl) {
            Write-Host 'Approve Funnel in the browser window, then return here.' -ForegroundColor Yellow
            Start-Process $approvalUrl
            if (-not $NoPause) {
                [void](Read-Host 'After approval, press Enter to continue')
            }
            $funnelResult = Invoke-TailscaleCommand `
                -Arguments @('funnel', '--bg', '--yes', 'http://127.0.0.1:8080') `
                -TimeoutSeconds 45
        }
        if ($funnelResult.ExitCode -ne 0) {
            $funnelError = $funnelResult.Output
            if ($funnelResult.TimedOut -and [string]::IsNullOrWhiteSpace($funnelError)) {
                $funnelError = 'Tailscale did not finish enabling Funnel within 45 seconds. Open the Tailscale window, approve Funnel if requested, and run this shortcut again.'
            }
            throw "The HTTPS test address could not be enabled. $funnelError"
        }
    }

    $status = Get-TailscaleStatus
    $dnsName = if ($status -and $status.Self -and $status.Self.DNSName) {
        $status.Self.DNSName.Trim().TrimEnd('.')
    } else {
        ''
    }
    if (-not $dnsName -or $dnsName -notmatch '\.ts\.net$') {
        throw 'The permanent Tailscale test address could not be determined.'
    }

    $publicUrl = "https://$dnsName"
    $config.Synchronization.AdvertisedBaseUrl = $publicUrl
    $config.Synchronization.AllowInsecureHttp = $false
    $config | ConvertTo-Json -Depth 20 | Set-Content $configPath -Encoding UTF8

    Set-Service -Name 'FleetSafetyManager' -StartupType Automatic
    Restart-Service -Name 'FleetSafetyManager'

    $deadline = (Get-Date).AddSeconds(60)
    $publicReady = $false
    do {
        $publicReady = Test-FsmSynchronizationHealth -Uri "$publicUrl/api/synchronization/health"
        if (-not $publicReady) { Start-Sleep -Seconds 3 }
    } while (-not $publicReady -and (Get-Date) -lt $deadline)

    if (-not $publicReady) {
        throw "The test address was created but its FSM health check failed: $publicUrl"
    }

    $urlFile = Join-Path $appRoot 'FSM-Test-Connection-URL.txt'
    Set-Content -Path $urlFile -Value $publicUrl -Encoding ASCII
    try { Set-Clipboard -Value $publicUrl } catch { }

    Write-Host ''
    Write-Host 'FSM TEST CONNECTION IS READY' -ForegroundColor Green
    Write-Host $publicUrl -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'The address was copied to the clipboard and saved here:'
    Write-Host $urlFile
    Write-Host 'Send only this address to the installer builder. No password or token is required.'
    Wait-ForEnter
    exit 0
} catch {
    Write-Host ''
    Write-Host 'FSM test connection setup failed:' -ForegroundColor Red
    $errorMessage = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($errorMessage)) {
        $errorMessage = ($_ | Out-String).Trim()
    }
    if ([string]::IsNullOrWhiteSpace($errorMessage)) {
        $errorMessage = 'An unexpected error occurred. Run the setup again.'
    }
    Write-Host $errorMessage -ForegroundColor Red
    Write-Host ''
    Wait-ForEnter
    exit 1
}
