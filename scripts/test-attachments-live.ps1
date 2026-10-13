# Live check for PLAN_FULL.md 5.1 against the running API (http://localhost:5210):
# upload a real PNG to a saved visit, list it back, open it, and prove a fake PNG is refused.
$base = 'http://localhost:5210'

$login = Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' -Body '{"username":"doctor","password":"CHANGE-ME"}'
$h = @{ Authorization = "Bearer $($login.token)" }
"login: $($login.user.role) / $($login.clinic.slug)"

$patient = (Invoke-RestMethod -Uri "$base/api/patients" -Headers $h)[0]
$visits = Invoke-RestMethod -Uri "$base/api/visits/by-patient/$($patient.id)" -Headers $h
if (-not $visits) { 'no saved visit yet - create one from the UI first'; exit 1 }
$visitId = $visits[0].id
"patient=$($patient.id) visit=$visitId"

# 1x1 PNG: magic + IHDR + minimal IDAT
$png = [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A, 0,0,0,0x0D,0x49,0x48,0x44,0x52, 0,0,0,1, 0,0,0,1, 8,6,0,0,0, 0x49,0x44,0x41,0x54, 0x28,0xCE,0x49,0,1,0,2)

function Add-Part($boundary, $fileName, $bytes) {
    $crlf = [System.Text.Encoding]::ASCII.GetBytes("`r`n")
    $head = [System.Text.Encoding]::ASCII.GetBytes("--$boundary`r`nContent-Disposition: form-data; name=`"file`"; filename=`"$fileName`"`r`nContent-Type: image/png`r`n`r`n")
    $tail = [System.Text.Encoding]::ASCII.GetBytes("`r`n--$boundary--`r`n")
    return $head + $bytes + $tail
}

function Invoke-Upload($url, $headers, $fileName, $bytes) {
    $boundary = [System.Guid]::NewGuid().ToString('N')
    $body = Add-Part $boundary $fileName $bytes
    return Invoke-WebRequest -Uri $url -Method Post -Headers $headers -ContentType "multipart/form-data; boundary=$boundary" -Body $body -UseBasicParsing
}

$up = Invoke-Upload "$base/api/visits/$visitId/attachments" $h 'live-test-5-1.png' $png
"upload: $($up.StatusCode)"
$att = $up.Content | ConvertFrom-Json
"attachment id=$($att.id) name=$($att.originalName) type=$($att.contentType) bytes=$($att.sizeBytes)"

$list = Invoke-RestMethod -Uri "$base/api/visits/$visitId/attachments" -Headers $h
"list: $($list.Count) attachment(s) on the visit; newest id=$($list[-1].id)"

$open = Invoke-WebRequest -Uri "$base/api/visits/attachments/$($att.id)" -Headers $h -UseBasicParsing
"open: $($open.StatusCode) content-type=$($open.Headers['Content-Type']) bytes=$($open.RawContentLength)"
if ($open.RawContentLength -ne $png.Length) { 'BYTE MISMATCH!'; exit 1 }

try {
    $fake = Invoke-Upload "$base/api/visits/$visitId/attachments" $h 'fake.png' ([System.Text.Encoding]::ASCII.GetBytes('this is not a png at all'))
    "fake png answered $($fake.StatusCode) (expected 400)"
} catch {
    "fake png refused: $([int]$_.Exception.Response.StatusCode)"
}

$folder = 'd:\ClinicMVP\backend\TechCare.Clinic.Api\PatientFiles'
if (Test-Path $folder) {
    "files on disk: " + (Get-ChildItem -Recurse $folder -File | Select-Object -ExpandProperty Name) -join ', '
} else {
    "attachment folder not found at $folder"
}

