<#
.SYNOPSIS
  Compares the widget's adapter ledger (today's exact per-connection totals in %AppData%\NetRouteWidget\usage.json) with
  the growth of the same adapters' Windows counters over a short window.
.DESCRIPTION
  Strictly read-only and needs no administrator rights: it reads usage.json, calls Get-NetAdapterStatistics and, if present,
  reads BitMeter 2's history file. It never writes a file and never touches the widget or the network configuration.
  Works in Windows PowerShell 5.1 and PowerShell 7.

  Run it while the widget (a build with the adapter ledger) is running and the connections carry some traffic, for example:
    powershell -File tools\Compare-AdapterLedger.ps1 -PhoneAdapter 'Ethernet 5' -LanAdapter 'Ethernet'

  For each connection it prints the growth of the Windows counters and of the ledger over -Seconds. PASS = the two agree
  within 5 % (the widget saves usage.json every 30 s, so the file can trail the live counters by up to that long; use a
  longer -Seconds if a run is borderline). A connection whose adapter grew by less than 100 KB is skipped: too little to compare.
  BitMeter's day total is printed for context only, without a verdict: its adapter selection may differ from NetRoute's.

  Exit codes: 0 = no FAIL and at least one PASS; 1 = at least one FAIL, or an error (for example a wrong adapter name, which
  stops the script before measuring); 2 = no PASS verdict at all (nothing comparable: no ledger data yet, adapters under
  100 KB, or a run that crossed midnight).
.PARAMETER PhoneAdapter
  Name of the phone adapter as in Get-NetAdapter (for example 'Ethernet 5').
.PARAMETER LanAdapter
  Name of the LAN adapter (for example 'Ethernet').
.PARAMETER WifiAdapter
  Optional name of the Wi-Fi adapter.
.PARAMETER Seconds
  Length of the measurement window. Default 90.
#>
param(
    [Parameter(Mandatory)][string]$PhoneAdapter,
    [Parameter(Mandatory)][string]$LanAdapter,
    [string]$WifiAdapter,
    [ValidateRange(1, 3600)][int]$Seconds = 90
)

$ErrorActionPreference = 'Stop'
$usageFile = Join-Path $env:APPDATA 'NetRouteWidget\usage.json'
$bitmeterFile = Join-Path $env:ProgramData 'Bitmeter2\BitmeterHistory.xml'
$minBytes = 100KB
$tolerance = 0.05
$invariant = [Globalization.CultureInfo]::InvariantCulture

# Connections to compare: ledger key in usage.json, label, adapter name.
$connections = @(
    [pscustomobject]@{ Key = 'phone'; Label = 'Phone'; Adapter = $PhoneAdapter }
    [pscustomobject]@{ Key = 'lan'; Label = 'LAN'; Adapter = $LanAdapter }
)
if ($WifiAdapter) { $connections += [pscustomobject]@{ Key = 'wifi'; Label = 'Wi-Fi'; Adapter = $WifiAdapter } }

function Format-Bytes([double]$Bytes) {
    $units = 'B', 'KB', 'MB', 'GB', 'TB'
    $i = 0
    while ([math]::Abs($Bytes) -ge 1024 -and $i -lt $units.Count - 1) { $Bytes /= 1024; $i++ }
    if ($i -eq 0) { return ('{0:N0} B' -f $Bytes) }
    return ('{0:N2} {1}' -f $Bytes, $units[$i])
}

# Today's ledger as @{ phone = <up+down>; ... } plus 'present' (the day has an adapters property) and 'since'.
# A connection without growth may be absent from the file; that counts as 0.
function Get-Ledger {
    $today = (Get-Date).ToString('yyyy-MM-dd', $invariant)
    $result = @{ present = $false; since = $null; day = $today; totals = @{ phone = 0L; lan = 0L; wifi = 0L } }
    if (-not (Test-Path -LiteralPath $usageFile)) { return $result }
    $json = $null
    for ($attempt = 1; $attempt -le 3 -and -not $json; $attempt++) {
        try { $json = Get-Content -LiteralPath $usageFile -Raw | ConvertFrom-Json }
        catch { Start-Sleep -Milliseconds 300 }   # the widget may be writing the file at this moment
    }
    if (-not $json -or -not $json.days) { return $result }
    $day = $json.days.$today
    if (-not $day) { return $result }
    $adapters = $day.adapters
    if (-not $adapters) { return $result }
    $result.present = $true
    $result.since = $adapters.since
    foreach ($key in 'phone', 'lan', 'wifi') {
        $part = $adapters.$key
        if ($part) { $result.totals[$key] = [long]$part.up + [long]$part.down }
    }
    return $result
}

function Get-AdapterCounters([string]$Name) {
    $stats = Get-NetAdapterStatistics -Name $Name -ErrorAction Stop
    return [pscustomobject]@{ Up = [long]$stats.SentBytes; Down = [long]$stats.ReceivedBytes }
}

# Fail early and clearly on a wrong adapter name.
foreach ($c in $connections) {
    try { $null = Get-AdapterCounters $c.Adapter }
    catch { throw ("Adapter '{0}' ({1}) was not found by Get-NetAdapterStatistics. Names as Windows shows them:`n{2}" -f $c.Adapter, $c.Label, ((Get-NetAdapter | ForEach-Object { '  ' + $_.Name }) -join "`n")) }
}

$ledgerBefore = Get-Ledger
$countersBefore = @{}
foreach ($c in $connections) { $countersBefore[$c.Key] = Get-AdapterCounters $c.Adapter }
Write-Host ("Measuring for {0} s (read-only). Keep some traffic going on the connections to compare." -f $Seconds)
Start-Sleep -Seconds $Seconds
$countersAfter = @{}
foreach ($c in $connections) { $countersAfter[$c.Key] = Get-AdapterCounters $c.Adapter }
$ledgerAfter = Get-Ledger

Write-Host ''
if ($ledgerAfter.present) {
    $sinceText = if ($ledgerAfter.since) { "counted since $($ledgerAfter.since) today" } else { 'start time not recorded' }
    Write-Host ("Ledger in usage.json for {0}: {1}." -f $ledgerAfter.day, $sinceText)
} else {
    Write-Host 'no ledger data in usage.json yet (no "adapters" for today). The widget is either not running, an older build without the ledger, or has not saved yet (it saves every 30 s).' -ForegroundColor Yellow
}
Write-Host ''

# The ledger is per day and the file is saved every 30 s: a run across midnight, or a ledger that only appeared during the
# run, would compare the counters with the wrong baseline and give a false result.
$noLedgerComparison = $null
if ($ledgerBefore.day -ne $ledgerAfter.day) {
    $noLedgerComparison = 'the run crossed midnight (the ledger day changed), so the ledger growth is not comparable. Run it again.'
} elseif ($ledgerAfter.present -and -not $ledgerBefore.present) {
    $noLedgerComparison = 'the ledger for today first appeared during the run, so there is no starting reading to compare against. Run it again.'
}

$failed = 0; $passed = 0; $skipped = 0
foreach ($c in $connections) {
    $b = $countersBefore[$c.Key]; $a = $countersAfter[$c.Key]
    $up = $a.Up - $b.Up; $down = $a.Down - $b.Down
    # A counter that went backwards means the adapter was reset during the window; nothing sensible to compare.
    $reset = ($up -lt 0 -or $down -lt 0)
    $grew = $up + $down
    $ledgerGrew = $ledgerAfter.totals[$c.Key] - $ledgerBefore.totals[$c.Key]

    Write-Host ("{0} ({1})" -f $c.Label, $c.Adapter) -ForegroundColor Cyan
    if ($reset) {
        Write-Host '  Windows counters went backwards (adapter reset during the window). SKIPPED.' -ForegroundColor Yellow
        $skipped++; continue
    }
    Write-Host ("  Windows counters grew: {0}  (up {1}, down {2})" -f (Format-Bytes $grew), (Format-Bytes $up), (Format-Bytes $down))
    if ($ledgerAfter.present) {
        Write-Host ("  Ledger grew:           {0}   (ledger total today {1}, Windows counters since boot {2})" -f (Format-Bytes $ledgerGrew), (Format-Bytes $ledgerAfter.totals[$c.Key]), (Format-Bytes ($a.Up + $a.Down)))
    } else {
        Write-Host '  Ledger grew:           no ledger data in usage.json yet'
    }

    if ($grew -lt $minBytes) {
        Write-Host ("  SKIPPED: the adapter grew by less than {0}, too little to compare." -f (Format-Bytes $minBytes)) -ForegroundColor Yellow
        $skipped++; continue
    }
    if (-not $ledgerAfter.present) {
        Write-Host '  NO VERDICT: no ledger data to compare.' -ForegroundColor Yellow
        $skipped++; continue
    }
    if ($noLedgerComparison) {
        Write-Host ("  NO VERDICT: {0}" -f $noLedgerComparison) -ForegroundColor Yellow
        $skipped++; continue
    }
    $ratio = $ledgerGrew / $grew
    $text = '  ledger / Windows = {0:P1}' -f $ratio
    if ([math]::Abs($ratio - 1) -le $tolerance) { Write-Host ($text + '  PASS') -ForegroundColor Green; $passed++ }
    else { Write-Host ($text + '  FAIL (outside 95 %..105 %; the file is saved every 30 s, so try a longer -Seconds before suspecting the ledger)') -ForegroundColor Red; $failed++ }
}

# BitMeter 2, context only. Today's day total = the item at bufferPointer of the download and upload day buffers.
Write-Host ''
if (Test-Path -LiteralPath $bitmeterFile) {
    try {
        $xml = [xml](Get-Content -LiteralPath $bitmeterFile -Raw)
        $day = 0L
        foreach ($name in 'dlDayBuffer', 'ulDayBuffer') {
            $node = $xml.DataHistory.$name
            $index = [int]$node.bufferPointer
            $day += [long]@($node.buffer.long)[$index]
        }
        Write-Host ("BitMeter day total (download + upload, from its last save): {0}" -f (Format-Bytes $day)) -ForegroundColor Cyan
        Write-Host '  Context only, no verdict: BitMeter''s adapter selection may differ from NetRoute''s (it may also count the NetRoute tunnel), and it saves only now and then.'
    } catch {
        Write-Host ("BitMeter's history file could not be read: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
    }
} else {
    Write-Host "BitMeter history not found at $bitmeterFile (context only, skipped)."
}

Write-Host ''
Write-Host ("Result: {0} PASS, {1} FAIL, {2} skipped or without verdict." -f $passed, $failed, $skipped)
if ($failed -gt 0) { exit 1 }
if ($passed -eq 0) { exit 2 }
exit 0
