param([switch]$Once, [int]$Seconds = 15)
# PubMed download progress tracker. Run:  powershell -ExecutionPolicy Bypass -File d:\ClinicMVP\pubmed-progress.ps1
# (Ctrl+C to stop watching - it never stops the download itself)

$rawDir   = 'd:\ClinicMVP\knowledge\medical_kb\pubmed\raw'
$chunkDir = 'd:\ClinicMVP\knowledge\medical_kb\chunks\pubmed'
$errLog   = 'd:\ClinicMVP\_pubmed.err.txt'
$outLog   = 'd:\ClinicMVP\_pubmed.out.txt'

while ($true) {
    $raw   = Get-ChildItem $rawDir   -File -ErrorAction SilentlyContinue
    $chunk = Get-ChildItem $chunkDir -File -ErrorAction SilentlyContinue
    $proc  = Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
             Where-Object { $_.CommandLine -like '*download_medical_kb*' }

    $bar = ((Get-Content $errLog -Tail 1 -ErrorAction SilentlyContinue) -join ' ') -replace '\s+', ' '
    $months = 0; $total = 208
    if ($bar -match '(\d+)/(\d+)\s*\[|\s*(\d+)/(\d+)\s+month') { $m = $Matches[1..4] | Where-Object { $_ }; if ($m.Count -ge 2) { $months = [int]$m[0]; $total = [int]$m[1] } }
    $pct = 0; if ($total -gt 0) { $pct = [Math]::Round(100 * $months / $total) }
    $done = ((Get-Content $outLog -Tail 3 -ErrorAction SilentlyContinue) -join ' ') -match 'Done\.'

    if (-not $Once) { try { Clear-Host } catch { } }
    Write-Host '===== PubMed download progress =====' -ForegroundColor Cyan
    if ($proc) { Write-Host "status      : RUNNING (pid $($proc.ProcessId))" -ForegroundColor Green }
    else       { Write-Host "status      : NOT RUNNING" -ForegroundColor Red }
    Write-Host ("months      : $months / $total  ($pct%)")
    Write-Host ("raw files   : " + $raw.Count + "  =  " + [Math]::Round(($raw | Measure-Object Length -Sum).Sum / 1MB, 1) + " MB")
    Write-Host ("latest month: " + $(if ($raw.Count) { ($raw | Sort-Object Name)[-1].Name } else { '-' }))
    Write-Host ("chunks      : " + $chunk.Count + " files  =  " + [Math]::Round(($chunk | Measure-Object Length -Sum).Sum / 1MB, 1) + " MB")
    Write-Host ("D: free     : " + [Math]::Round((Get-PSDrive D).Free / 1GB, 2) + " GB")
    Write-Host ''
    Write-Host 'last log line:' -ForegroundColor DarkGray
    Write-Host $bar
    if ($done) { Write-Host '' ; Write-Host 'DOWNLOAD FINISHED - chunks are ready' -ForegroundColor Green }
    if ($Once) { break }
    Start-Sleep -Seconds $Seconds
}
