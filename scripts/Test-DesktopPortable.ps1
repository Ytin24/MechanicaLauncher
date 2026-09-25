param([switch]$CloseDuringEntrance, [switch]$Tray, [string]$PortableDirectory, [switch]$PortableData, [switch]$TLauncherGate)
$ErrorActionPreference = 'Stop'
if ($CloseDuringEntrance -and $Tray) { throw 'Use either -CloseDuringEntrance or -Tray' }
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$portableDir = if ($PortableDirectory) { (Resolve-Path -LiteralPath $PortableDirectory).Path } else { Join-Path $projectRoot 'out\nitidus-portable' }
$dataDir = if ($PortableData) { Join-Path $portableDir 'data' } else { Join-Path $projectRoot ('out\ui-smoke-' + [Guid]::NewGuid().ToString('N')) }
if ($PortableData -and (-not (Test-Path -LiteralPath (Join-Path $portableDir 'portable.flag')) -or (Test-Path -LiteralPath $dataDir))) {
    throw 'Portable data smoke requires a fresh extracted build with portable.flag and no data folder'
}
New-Item -ItemType Directory -Path $dataDir | Out-Null
$settings = @{ username = 'MechanicaTest'; authMode = 'offline'; discordRpc = $false; language = 'ru' }
if ($TLauncherGate) {
    $fixture = Join-Path $dataDir '.tlauncher'
    New-Item -ItemType Directory -Path (Join-Path $fixture 'jre') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $dataDir 'TLegacy') | Out-Null
    $tlauncherFile = Join-Path $fixture 'tlauncher-2.0.properties'
    $legacyFile = Join-Path $dataDir 'TLegacy\tlauncher.properties'
    Set-Content -LiteralPath $tlauncherFile -Value 'server=https://tlauncher.org/client' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fixture 'jre\release') -Value 'private Java fixture' -Encoding UTF8
    Set-Content -LiteralPath $legacyFile -Value 'bootstrap.brand=legacy' -Encoding UTF8
    $settings.tlauncherScanDirectories = @($fixture, (Split-Path -Parent $legacyFile))
}
if (-not $Tray) { $settings.closeToTray = $false }
Set-Content -LiteralPath (Join-Path $dataDir 'settings.json') -Value ($settings | ConvertTo-Json) -Encoding UTF8
if ($Tray -and -not ('MechanicaTraySmoke' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MechanicaTraySmoke {
    [DllImport("user32.dll", EntryPoint="PostMessageW")]
    public static extern bool Post(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr window);
}
'@
}
function Wait-TrayState([scriptblock]$Condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while (-not (& $Condition)) {
        if ($launcher.HasExited) { throw ('Launcher exited unexpectedly: ' + $launcher.ExitCode) }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Tray window state did not settle' }
        Start-Sleep -Milliseconds 40
    }
}
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = Join-Path $portableDir 'MechanicaLauncher.exe'
$startInfo.WorkingDirectory = $portableDir
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
if ($PortableData) { $startInfo.EnvironmentVariables.Remove('MECHANICA_DATA_DIR') }
else { $startInfo.EnvironmentVariables['MECHANICA_DATA_DIR'] = $dataDir }
$launcher = [System.Diagnostics.Process]::Start($startInfo)
try {
    if ($CloseDuringEntrance) {
        $deadline = [DateTime]::UtcNow.AddSeconds(12)
        do {
            if ($launcher.WaitForExit(20)) { throw ('Launcher exited during startup: ' + $launcher.ExitCode) }
            $launcher.Refresh()
        } while ($launcher.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
        Start-Sleep -Milliseconds 80
    } elseif ($launcher.WaitForExit(12000)) { throw ('Launcher exited during startup: ' + $launcher.ExitCode + '; data: ' + $dataDir) }
    $launcher.Refresh()
    if ($launcher.MainWindowHandle -eq [IntPtr]::Zero) { throw ('No launcher window; data: ' + $dataDir) }
    Write-Output ('PASS startup: ' + $launcher.MainWindowTitle)
    if ($TLauncherGate) {
        $saved = Get-Content -LiteralPath (Join-Path $dataDir 'settings.json') -Raw | ConvertFrom-Json
        if ($saved.knownTLauncherDirectories -notcontains $fixture) { throw 'Startup did not record the TLauncher root for the gate' }
        if ($saved.knownTLauncherFiles -contains $legacyFile -or $saved.knownTLauncherDirectories -contains (Split-Path -Parent $legacyFile)) { throw 'Legacy was incorrectly detected as TLauncher' }
        if (-not (Test-Path -LiteralPath $tlauncherFile) -or -not (Test-Path -LiteralPath $legacyFile)) { throw 'Startup must not remove launcher files' }
        Write-Output 'PASS startup detects the blocking fixture, excludes Legacy and removes nothing'
    }
    $windowHandle = $launcher.MainWindowHandle
    if ($Tray) {
        $null = [MechanicaTraySmoke]::Post($windowHandle, 0x112, [IntPtr]0xf030, [IntPtr]::Zero)
        Wait-TrayState { [MechanicaTraySmoke]::IsZoomed($windowHandle) }
        $null = [MechanicaTraySmoke]::Post($windowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
        Wait-TrayState { -not [MechanicaTraySmoke]::IsWindowVisible($windowHandle) }
        if ($launcher.HasExited) { throw 'Close-to-tray terminated the launcher' }
        Write-Output 'PASS default close hides to tray and preserves the process'
    }
    if (-not $CloseDuringEntrance) {
      $second = [System.Diagnostics.Process]::Start($startInfo)
      try {
        if (-not $second.WaitForExit(10000)) { throw 'Second launcher did not forward to the first instance' }
        if ($second.ExitCode -ne 0) { throw ('Second instance exit: ' + $second.ExitCode) }
        $pending = Join-Path $dataDir 'pending_connect.txt'
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while ((Test-Path -LiteralPath $pending) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (Test-Path -LiteralPath $pending) { throw 'Primary launcher did not consume the forwarded command' }
        Write-Output 'PASS single-instance forwarding'
        if ($Tray) {
            Wait-TrayState { [MechanicaTraySmoke]::IsWindowVisible($windowHandle) }
            if (-not [MechanicaTraySmoke]::IsZoomed($windowHandle)) { throw 'Tray restore lost maximized state' }
            Write-Output 'PASS second launch restores the maximized tray window'
        }
      }
      finally { if (-not $second.HasExited) { $second.Kill() }; $second.Dispose() }
    }
    if ($Tray) {
        $null = [MechanicaTraySmoke]::Post($windowHandle, 0x16, [IntPtr]1, [IntPtr]::Zero)
    } elseif (-not $launcher.CloseMainWindow()) { throw 'Could not request launcher close' }
    if (-not $launcher.WaitForExit(10000)) { throw 'Launcher did not close' }
    if ($launcher.ExitCode -ne 0) { throw ('Launcher exit: ' + $launcher.ExitCode) }
    if (Test-Path -LiteralPath (Join-Path $dataDir 'logs\launcher-errors.log')) { throw ('Launcher wrote a crash log: ' + $dataDir) }
    Write-Output 'PASS clean launcher exit'
    Write-Output ('Isolated data: ' + $dataDir)
}
finally { if (-not $launcher.HasExited) { $launcher.Kill() }; $launcher.Dispose() }
