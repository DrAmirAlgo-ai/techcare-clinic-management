# API smoke test against a running backend (default http://localhost:5210, Development seed accounts).
param([string]$Host_ = "http://localhost:5210")
$ErrorActionPreference = "Stop"

function Call($method, $path, $body = $null, $token = $null) {
    $headers = @{}
    if ($token) { $headers["Authorization"] = "Bearer $token" }
    $req = @{ Method = $method; Uri = "$Host_$path"; Headers = $headers; ContentType = "application/json; charset=utf-8" }
    if ($null -ne $body) { $req["Body"] = [System.Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 6)) }
    try { return Invoke-RestMethod @req }
    catch {
        return [pscustomobject]@{ __status = $_.Exception.Response.StatusCode.value__; __error = $_.ErrorDetails.Message }
    }
}

$doc = Call POST "/api/auth/login" @{ username = "doctor"; password = "CHANGE-ME" }
$rec = Call POST "/api/auth/login" @{ username = "reception"; password = "CHANGE-ME" }
"login doctor: role=$($doc.user.role) clinic=$($doc.clinic.slug)"
"login reception: role=$($rec.user.role)"
"bad login -> $((Call POST '/api/auth/login' @{ username = 'doctor'; password = 'wrong' }).__status)"

$p = Call POST "/api/patients" @{ fullName = "مريض تجريبي"; phone = "01011112222"; gender = "ذكر"; birthDate = "1975-03-10"; chronicConditions = "سكر نوع 2، ضغط"; allergies = "Penicillin" } $doc.token
"patient created: id=$($p.id) code=$($p.code)"
$recView = Call GET "/api/patients/$($p.id)" $null $rec.token
"reception sees allergies? $([bool]$recView.allergies)"
"reception -> visits: $((Call GET "/api/visits/by-patient/$($p.id)" $null $rec.token).__status)"

$walk = Call POST "/api/queue/walk-in" @{ patientId = $p.id; type = "Consultation"; isUrgent = $false } $rec.token
"walk-in queue number: $($walk.queueNumber)"
$next = Call POST "/api/queue/next" $null $doc.token
"called next: $($next.patientName) #$($next.queueNumber)"

$visit = Call POST "/api/visits" @{
    patientId = $p.id; appointmentId = $walk.id; chiefComplaint = "صداع ودوخة"; diagnosis = "Uncontrolled hypertension"
    bloodPressure = "160/100"; pulse = 88; bloodSugar = 210; weightKg = 92
    prescription = @(
        @{ drugName = "Amlodipine 5mg"; dose = "1 قرص"; frequency = "مرة يومياً"; duration = "شهر" },
        @{ drugName = "Metformin 500mg"; dose = "1 قرص"; frequency = "مرتين يومياً"; duration = "شهر" })
} $doc.token
"visit created: $($visit.id)"
$q = Call GET "/api/queue" $null $rec.token
"queue after visit: current=$($q.current.queueNumber) waiting=$($q.waitingCount) doneToday=$($q.doneToday)"

$inv = Call POST "/api/invoices" @{ patientId = $p.id; visitId = $visit.id; description = "كشف"; amount = 300; discount = 50; paid = 250; method = "Cash" } $rec.token
"invoice: $($inv.id)"
$dash = Call GET "/api/dashboard" $null $doc.token
"dashboard: patients=$($dash.totalPatients) revenue=$($dash.revenueToday) done=$($dash.done)"

$profile = Call GET "/api/public/clinics/dr-amir"
"public profile: $($profile.doctorName) / $($profile.specialty)"
$date = (Get-Date).AddDays(1)
for ($i = 0; $i -lt 7 -and -not (Call GET "/api/public/clinics/dr-amir/slots?date=$($date.ToString('yyyy-MM-dd'))").slots.Count; $i++) { $date = $date.AddDays(1) }
$slots = Call GET "/api/public/clinics/dr-amir/slots?date=$($date.ToString('yyyy-MM-dd'))"
"slots on $($date.ToString('yyyy-MM-dd')): $($slots.slots.Count) first=$($slots.slots[0])"
$book = Call POST "/api/public/clinics/dr-amir/bookings" @{ name = "مريضة تجريبية"; phone = "01122223333"; date = $date.ToString('yyyy-MM-dd'); time = $slots.slots[0]; type = "Consultation" }
"booking token: $($book.trackingToken)"
$dup = Call POST "/api/public/clinics/dr-amir/bookings" @{ name = "مريضة تجريبية"; phone = "01122223333"; date = $date.ToString('yyyy-MM-dd'); time = $slots.slots[1]; type = "Consultation" }
"duplicate booking -> $($dup.__status)"
$track = Call GET "/api/public/track/$($book.trackingToken)"
"track: name=$($track.patientName) status=$($track.status) at=$($track.scheduledAt)"
$display = Call GET "/api/public/clinics/dr-amir/queue"
"display: waiting=$($display.waitingCount) avg=$($display.avgMinutesPerPatient)"

"no token -> patients: $((Call GET '/api/patients').__status)"
"reception -> users (admin only): $((Call GET '/api/users' $null $rec.token).__status)"
$ai = Call GET "/api/ai/status" $null $doc.token
"ai: reachable=$($ai.reachable) chat=$($ai.chatModelLoaded) embed=$($ai.embeddingModelLoaded)"

