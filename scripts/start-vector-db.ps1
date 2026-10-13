<#
    يشغّل قاعدة PostgreSQL + pgvector اللي بيستخدمها البحث في المكتبة الطبية.
    شغّله مرة واحدة (لازم Docker Desktop يبقى مفتوح). البيانات بتفضل محفوظة في volume بعد أي إعادة تشغيل.

    بعد التشغيل، سلسلة الاتصال بتبقى:
        Host=127.0.0.1;Port=5433;Database=medical_kb;Username=postgres;Password=CHANGE-ME
    وهي مضبوطة أصلاً في appsettings.Development.json
#>
param(
    [int]$Port = 5433,
    [string]$Password = 'techcare',
    [string]$Container = 'techcare-vector-db',
    [string]$Volume = 'techcare-vector-data'
)

# docker بيكتب رسايله على stderr، و PowerShell 5.1 بيحوّلها لخطأ قاتل لما ErrorActionPreference = 'Stop'،
# فبنتأكد من كل أمر عن طريق $LASTEXITCODE بدل كده.
$ErrorActionPreference = 'Continue'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker مش مثبّت. ثبّت Docker Desktop ثم شغّل السكربت تاني.'
}

# التأكد إن الـ daemon شغال
$probe = docker info 2>&1
if ($LASTEXITCODE -ne 0) {
    throw 'Docker Desktop مش شغال. افتحه واستنى لحد ما يجهز، بعدين شغّل السكربت تاني.'
}

$nameFilter = 'name=^/' + $Container + '$'
$exists = docker ps -a --filter $nameFilter --format '{{.Names}}' 2>&1
if ($exists) {
    $running = docker ps --filter $nameFilter --format '{{.Names}}' 2>&1
    if ($running) {
        Write-Host "قاعدة المتجهات شغالة بالفعل على المنفذ $Port" -ForegroundColor Green
    }
    else {
        Write-Host 'بنشغّل الحاوية الموجودة...' -ForegroundColor Cyan
        docker start $Container 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'فشل تشغيل الحاوية الموجودة.' }
    }
}
else {
    Write-Host 'بننزّل صورة pgvector وننشئ الحاوية (ممكن تاخد كام دقيقة أول مرة)...' -ForegroundColor Cyan
    $run = docker run -d --name $Container `
        -e "POSTGRES_PASSWORD=$Password" `
        -e 'POSTGRES_DB=medical_kb' `
        -p "${Port}:5432" `
        -v "${Volume}:/var/lib/postgresql/data" `
        --restart unless-stopped `
        'pgvector/pgvector:pg17' 2>&1
    if ($LASTEXITCODE -ne 0) { throw "فشل إنشاء الحاوية: $run" }
}

Write-Host 'بنسنّى PostgreSQL يجهز...' -ForegroundColor Cyan
$ready = $false
for ($i = 0; $i -lt 120; $i++) {
    docker exec $Container pg_isready -U postgres -d medical_kb 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 1
}

if (-not $ready) {
    $logs = docker logs --tail 20 $Container 2>&1
    throw "PostgreSQL مجهزش في الوقت المحدد:`n$logs"
}

Write-Host 'تم التشغيل.' -ForegroundColor Green
Write-Host "سلسلة الاتصال: Host=127.0.0.1;Port=$Port;Database=medical_kb;Username=postgres;Password=$Password"

