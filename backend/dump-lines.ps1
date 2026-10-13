param([string]$File, [int]$Start, [int]$End, [string]$Out)
$t = [System.IO.File]::ReadAllText($File)
$l = $t -split "`r?`n"
$take = @()
for ($i = $Start - 1; $i -le ($End - 1) -and $i -lt $l.Count; $i++) { $take += $l[$i] }
[System.IO.File]::WriteAllText($Out, ($take -join "`n"), [System.Text.UTF8Encoding]::new($false))
