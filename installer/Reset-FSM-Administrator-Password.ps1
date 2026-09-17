$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Show-Error([string]$Message) {
    [System.Windows.Forms.MessageBox]::Show(
        $Message,
        "Fleet Safety Manager",
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
        Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -Verb RunAs | Out-Null
    }
    catch {
        Show-Error "Windows administrator approval is required to reset the password."
    }
    exit
}

try {
    $applicationRoot = Split-Path -Parent $PSCommandPath
    $currentFile = Join-Path $applicationRoot "current.txt"
    if (-not (Test-Path $currentFile)) {
        throw "The active Fleet Safety Manager release could not be found. Reinstall Office Setup first."
    }

    $currentRelease = (Get-Content $currentFile -Raw).Trim()
    $releasePath = if ([IO.Path]::IsPathRooted($currentRelease)) {
        $currentRelease
    }
    else {
        Join-Path $applicationRoot $currentRelease
    }
    $applicationExe = Join-Path $releasePath "FSM.Api.exe"
    if (-not (Test-Path $applicationExe)) {
        throw "FSM.Api.exe is missing from the active release. Reinstall Office Setup first."
    }

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Reset FSM Administrator Password"
    $form.StartPosition = "CenterScreen"
    $form.ClientSize = New-Object System.Drawing.Size(460, 250)
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.TopMost = $true

    $heading = New-Object System.Windows.Forms.Label
    $heading.Text = "Enter a new Office Administrator password"
    $heading.Font = New-Object System.Drawing.Font("Segoe UI", 12, [System.Drawing.FontStyle]::Bold)
    $heading.Location = New-Object System.Drawing.Point(24, 20)
    $heading.AutoSize = $true
    $form.Controls.Add($heading)

    $note = New-Object System.Windows.Forms.Label
    $note.Text = "The password must contain at least 8 characters."
    $note.Location = New-Object System.Drawing.Point(24, 54)
    $note.AutoSize = $true
    $form.Controls.Add($note)

    $firstLabel = New-Object System.Windows.Forms.Label
    $firstLabel.Text = "New password"
    $firstLabel.Location = New-Object System.Drawing.Point(24, 88)
    $firstLabel.AutoSize = $true
    $form.Controls.Add($firstLabel)

    $first = New-Object System.Windows.Forms.TextBox
    $first.Location = New-Object System.Drawing.Point(180, 84)
    $first.Size = New-Object System.Drawing.Size(250, 28)
    $first.UseSystemPasswordChar = $true
    $form.Controls.Add($first)

    $secondLabel = New-Object System.Windows.Forms.Label
    $secondLabel.Text = "Repeat password"
    $secondLabel.Location = New-Object System.Drawing.Point(24, 128)
    $secondLabel.AutoSize = $true
    $form.Controls.Add($secondLabel)

    $second = New-Object System.Windows.Forms.TextBox
    $second.Location = New-Object System.Drawing.Point(180, 124)
    $second.Size = New-Object System.Drawing.Size(250, 28)
    $second.UseSystemPasswordChar = $true
    $form.Controls.Add($second)

    $reset = New-Object System.Windows.Forms.Button
    $reset.Text = "Change Password"
    $reset.Location = New-Object System.Drawing.Point(180, 180)
    $reset.Size = New-Object System.Drawing.Size(150, 36)
    $reset.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $reset
    $form.Controls.Add($reset)

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = "Cancel"
    $cancel.Location = New-Object System.Drawing.Point(340, 180)
    $cancel.Size = New-Object System.Drawing.Size(90, 36)
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancel
    $form.Controls.Add($cancel)

    while ($true) {
        if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            exit
        }
        if ($first.Text.Length -lt 8) {
            Show-Error "The new password must contain at least 8 characters."
            continue
        }
        if ($first.Text -cne $second.Text) {
            Show-Error "The passwords do not match. Please enter them again."
            $second.Clear()
            continue
        }
        break
    }

    $service = Get-Service "FleetSafetyManager" -ErrorAction SilentlyContinue
    $restartService = $service -and $service.Status -eq "Running"
    if ($restartService) {
        Stop-Service "FleetSafetyManager" -Force
        (Get-Service "FleetSafetyManager").WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
    }

    try {
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $applicationExe
        $startInfo.Arguments = "--reset-administrator-password"
        $startInfo.WorkingDirectory = $releasePath
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw "The password reset process could not be started."
        }
        $process.StandardInput.WriteLine($first.Text)
        $process.StandardInput.WriteLine($second.Text)
        $process.StandardInput.Close()
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            $detail = if ($standardError.Trim()) { $standardError.Trim() } else { $standardOutput.Trim() }
            throw "The administrator password could not be changed. $detail"
        }
    }
    finally {
        $first.Clear()
        $second.Clear()
        if ($restartService) {
            Start-Service "FleetSafetyManager"
            (Get-Service "FleetSafetyManager").WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
        }
    }

    [System.Windows.Forms.MessageBox]::Show(
        "The Office Administrator password was changed successfully. You can now sign in with the new password.",
        "Fleet Safety Manager",
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
}
catch {
    Show-Error $_.Exception.Message
}
