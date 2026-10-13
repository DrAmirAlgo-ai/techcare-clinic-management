# Start the TechCare Clinic system and open it in the browser.
# Called by START-CLINIC.bat (kept ASCII-only: PowerShell 5.1 mis-reads Arabic in .ps1 files).
$ErrorActionPreference = 'SilentlyContinue'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'backend\TechCare.Clinic.Api'
$url = 'http://localhost:5210/'

function Test-ClinicUp {
    (Get-NetTCPConnection -State Listen -LocalPort 5210 -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
}

if (-not (Test-ClinicUp)) {
    Write-Host 'Starting TechCare Clinic, please wait (about 20 seconds)...'
    Start-Process -FilePath 'dotnet' -ArgumentList 'run' -WorkingDirectory $proj -WindowStyle Minimized

    for ($i = 0; $i -lt 40 -and -not (Test-ClinicUp); $i++) {
        Start-Sleep -Seconds 2
    }
}

if (Test-ClinicUp) {
    Start-Process $url
    Write-Host 'Done - the system should be open in your browser now.'
    Write-Host 'If the page looks blank or old, press Ctrl+Shift+R in the browser.'
    Start-Sleep -Seconds 3
}
else {
    Write-Host 'The system took too long to start. Close this window and run START-CLINIC.bat again.'
    Read-Host 'Press Enter to exit'
}

