# AI assistant regression check against a running backend (Development seed accounts).
# Usage: powershell -File scripts/ai-test.ps1 -Out d:\ClinicMVP\_ai_before.txt
param(
    [string]$Host_ = "http://localhost:5210",
    [string]$Out = "d:\ClinicMVP\_ai_out.txt",
    [string]$TokenFile = "d:\ClinicMVP\_token.txt",
    [int]$TimeoutSec = 600
)
$ErrorActionPreference = "Stop"

function Call($method, $path, $body = $null, $token = $null) {
    $headers = @{}
    if ($token) { $headers["Authorization"] = "Bearer $token" }
    $req = @{ Method = $method; Uri = "$Host_$path"; Headers = $headers; ContentType = "application/json; charset=utf-8"; TimeoutSec = $TimeoutSec }
    if ($null -ne $body) { $req["Body"] = [System.Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 6)) }
    try { return Invoke-RestMethod @req }
    catch {
        return [pscustomobject]@{ __status = $_.Exception.Response.StatusCode.value__; __error = $_.ErrorDetails.Message }
    }
}

$questions = @(
    @{ id = "1-painkiller-plus-ibuprofen"; q = "مسكن مع بروفين، ينفع أستخدمهم مع بعض؟ وإيه البدائل؟" },
    @{ id = "2-amoxicillin-child-12kg";    q = "جرعة أموكسيسيلين لطفل وزنه 12 كجم؟" },
    @{ id = "3-warfarin-aspirin";          q = "إيه تداخل وارفارين مع أسبرين؟" },
    @{ id = "4-fake-drug";                 q = "إيه جرعة دواء زيلوبراكس 999؟" },
    @{ id = "5-acute-chest-pain";          q = "ألم حاد في الصدر مع ضيق في التنفس، إيه الاحتمالات؟" },
    @{ id = "6-arabic-brands";             q = "كويتابيكس وسيدوفاج، إيه استخداماتهم؟" }
)

function Get-Token {
    if (Test-Path $TokenFile) {
        $t = (Get-Content $TokenFile -Raw).Trim()
        if ($t) {
            $probe = Call GET "/api/ai/status" $null $t
            if ($null -eq $probe.__status) { return @{ Token = $t; Who = "token-file" } }
        }
    }
    foreach ($c in @(@{ u = "doctor"; p = "CHANGE-ME" }, @{ u = "admin"; p = "CHANGE-ME" }, @{ u = "reception"; p = "CHANGE-ME" })) {
        $r = Call POST "/api/auth/login" @{ username = $c.u; password = $c.p }
        if ($null -eq $r.__status -and $r.token) { return @{ Token = $r.token; Who = "login:$($c.u)/$($r.user.role)" } }
    }
    throw "No working credentials: $TokenFile is missing/expired and the seed logins were rejected."
}

$auth = Get-Token
$token = $auth.Token
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("=== AI ASSISTANT CHECK  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  host=$Host_ ===")
$lines.Add("auth: $($auth.Who)")

$st = Call GET "/api/ai/status" $null $token
$lines.Add("ai status: reachable=$($st.reachable) chatModel=$($st.chatModel)/loaded=$($st.chatModelLoaded) embedModel=$($st.embeddingModel)/loaded=$($st.embeddingModelLoaded) vectorStoreReady=$($st.vectorStore.ready)")

foreach ($item in $questions) {
    $lines.Add("")
    $lines.Add("################ Q[$($item.id)] : $($item.q)")
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Call POST "/api/ai/ask" @{ question = $item.q; patientId = $null } $token
    $sw.Stop()
    if ($null -ne $r.__status) {
        $lines.Add("!!! HTTP $($r.__status): $($r.__error)")
        $lines | Set-Content $Out -Encoding utf8
        continue
    }
    $found = if ($null -ne $r.foundInLibrary) { $r.foundInLibrary } else { "n/a" }
    $lines.Add("--- ANSWER ($([int]$sw.Elapsed.TotalSeconds)s) foundInLibrary=$found ---")
    $lines.Add($r.answer)
    $lines.Add("--- SOURCES ($($r.sources.Count)) ---")
    $i = 0
    foreach ($s in $r.sources) {
        $i++
        $score = if ($null -ne $s.score) { " score=$($s.score)" } else { "" }
        $type = if ($s.sourceType) { " [$($s.sourceType)]" } else { "" }
        $lines.Add("[$i]$type$score | $($s.documentTitle) | $($s.section)")
        if ($s.url) { $lines.Add("      $($s.url)") }
    }
    $lines | Set-Content $Out -Encoding utf8
}

$lines | Set-Content $Out -Encoding utf8
Write-Output "written: $Out ($($lines.Count) lines)"
"done $(Get-Date -Format 'HH:mm:ss')" | Set-Content "$Out.done" -Encoding utf8

