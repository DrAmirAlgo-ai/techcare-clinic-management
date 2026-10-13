# PLAN.md 2.8 — evaluation log for the retrieval engine.
# Runs the 30 clinic questions in eval-questions.json against a RUNNING backend that has the dev library
# indexed and LM Studio loaded, and counts the answers that cited a source of the expected type.
#
# Usage:  powershell -NoProfile -File scripts\eval-ai.ps1 [-Host_ http://localhost:5210]
# Needs:  the API running (dotnet run), doctor/CHANGE-ME (Development seed) able to log in.

param(
    [string]$Host_ = "http://localhost:5210",
    [string]$Username = "doctor",
    [string]$Password = "CHANGE-ME"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$questions = @(Get-Content (Join-Path $here "eval-questions.json") -Raw -Encoding UTF8 | ConvertFrom-Json)

$body = @{ username = $Username; password = $Password } | ConvertTo-Json
$login = try {
    Invoke-RestMethod -Method Post -Uri "$Host_/api/auth/login" -Body $body -ContentType "application/json; charset=utf-8"
} catch {
    Write-Host "تعذّر تسجيل الدخول على $Host_ — شغّل الخادم أولاً (dotnet run)."
    exit 1
}
$hdr = @{ Authorization = "Bearer $($login.token)" }

$pass = 0
$total = 0

foreach ($q in $questions) {
    $total++
    $askBody = @{ question = $q.question } | ConvertTo-Json
    $r = try {
        Invoke-RestMethod -Method Post -Uri "$Host_/api/ai/ask" -Headers $hdr -Body $askBody -ContentType "application/json; charset=utf-8"
    } catch {
        Write-Host ("{0,-14}  فشل الطلب  {1}" -f $q.expect, $q.question)
        continue
    }

    # A source counts as cited only when the answer actually points at it with [n].
    $cited = [regex]::Matches($r.answer, "\[(\d+)\]") | ForEach-Object { [int]$_.Groups[1].Value }
    $ok = @($r.sources | Where-Object { $cited -contains $_.n -and $_.sourceType -eq $q.expect })
    if ($ok.Count -gt 0) {
        $pass++
        Write-Host ("{0,-14}  ✓  {1}" -f $q.expect, $q.question)
    } else {
        $used = @($r.sources | ForEach-Object { "$($_.n):$($_.sourceType)" }) -join ", "
        Write-Host ("{0,-14}  ✗  {1}   (المصادر: {2})" -f $q.expect, $q.question, $used)
    }
}

Write-Host "---"
Write-Host "النتيجة: {0}/{1} سؤالاً cited المصدر المتوقع." -f $pass, $total

