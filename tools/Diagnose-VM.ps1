param(
    [string]$ExePath = ""
)

$ErrorActionPreference = "Continue"

Write-Output "=== OS ==="
Get-CimInstance Win32_OperatingSystem |
    Select-Object Caption, Version, OSArchitecture | Format-List
Write-Output ("Processor architecture: " + $env:PROCESSOR_ARCHITECTURE)

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    Write-Output ""
    Write-Output "Usage:"
    Write-Output "  powershell -ExecutionPolicy Bypass -File Diagnose-VM.ps1 -ExePath 'C:\EVE\publish\EVE预警.exe'"
    exit 1
}

$folder = Split-Path -Parent $ExePath
Write-Output ""
Write-Output "=== EXE ==="
Write-Output ("Path   : " + $ExePath)
Write-Output ("Exists : " + (Test-Path $ExePath))
Write-Output ("Folder : " + $folder)

Write-Output ""
Write-Output "=== REQUIRED FILES (self-contained publish) ==="
foreach ($name in @("coreclr.dll", "hostfxr.dll", "EVE预警.Shared.dll", "NAudio.dll", "Emgu.CV.dll", "PaddleOCR.dll")) {
    $full = Join-Path $folder $name
    Write-Output ("{0,-24} {1}" -f $name, (Test-Path $full))
}

Write-Output ""
Write-Output "=== APP DATA ==="
$appDir = Join-Path $env:APPDATA "EVE预警"
if (Test-Path $appDir) {
    Get-ChildItem $appDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
} else {
    Write-Output ("Not found: " + $appDir)
}

$startupLog = Join-Path $appDir "startup.log"
if (Test-Path $startupLog) {
    Write-Output "--- startup.log ---"
    Get-Content $startupLog -Encoding UTF8
} else {
    Write-Output "startup.log: NOT FOUND (Main() never ran)"
}

$errorLog = Join-Path $appDir "startup-error.log"
if (Test-Path $errorLog) {
    Write-Output "--- startup-error.log (tail) ---"
    Get-Content $errorLog -Tail 40 -Encoding UTF8
} else {
    Write-Output "startup-error.log: none"
}

Write-Output ""
Write-Output "=== EVENT LOG (last 1 day) ==="
Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = (Get-Date).AddDays(-1) } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match "EVE|\.NET Runtime|Application Error" } |
    Select-Object -First 15 TimeCreated, ProviderName, Id, LevelDisplayName |
    Format-Table -AutoSize

Write-Output ""
Write-Output "=== LAUNCH TEST (5 seconds) ==="
try {
    $p = Start-Process -FilePath $ExePath -PassThru
    Start-Sleep -Seconds 5
    if ($p.HasExited) {
        Write-Output ("Exited, ExitCode = " + $p.ExitCode)
    } else {
        Write-Output "Still running after 5s (a window should be visible)"
        Stop-Process -Id $p.Id -Force
    }
} catch {
    Write-Output ("Launch failed: " + $_.Exception.Message)
}

Write-Output ""
Write-Output "=== DONE ==="
