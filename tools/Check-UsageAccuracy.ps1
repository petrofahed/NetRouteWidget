<#
.SYNOPSIS
  Checks the Usage tab's accuracy on this machine: downloads a known number of bytes and compares them with what the
  widget recorded in %AppData%\NetRouteWidget\usage.json.
.DESCRIPTION
  Run it with Smart routing ON and the widget running, on a quiet connection (other downloads add to "Unattributed").
  It only reads usage.json; it never touches the widget, sing-box or the network configuration.
  PASS = (the row for -RowKey + the "unattributed" row, phone + LAN) grew by 90%..110% of the downloaded bytes.
#>
param(
    [string]$Url = 'https://www.youtube.com/',
    [int]$Count = 6,
    [string]$RowKey = 'youtube',
    [int]$WaitSeconds = 40
)

$ErrorActionPreference = 'Stop'
$file = Join-Path $env:APPDATA 'NetRouteWidget\usage.json'
$today = (Get-Date).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)

function Get-Recorded {
    if (-not (Test-Path -LiteralPath $file)) { return 0L }
    $json = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $day = $json.days.$today
    if (-not $day) { return 0L }
    $sum = 0L
    foreach ($key in @($RowKey, 'unattributed')) {
        $row = $day.rows.$key
        if ($row) { $sum += [long]$row.phone.up + [long]$row.phone.down + [long]$row.lan.up + [long]$row.lan.down }
    }
    return $sum
}

if (-not (Get-Process NetRouteWidget -ErrorAction SilentlyContinue)) { throw 'The widget is not running.' }

Write-Host "Waiting for the widget to save the current numbers..."
Start-Sleep -Seconds $WaitSeconds
$before = Get-Recorded

$downloaded = 0L
for ($i = 1; $i -le $Count; $i++) {
    $bytes = & curl.exe -s -L -o NUL -w '%{size_download}' --max-time 30 $Url
    $downloaded += [long]$bytes
}
Write-Host ("Downloaded {0:N0} bytes in {1} requests from {2}" -f $downloaded, $Count, $Url)

Write-Host "Waiting for the widget to save (up to $WaitSeconds s)..."
Start-Sleep -Seconds $WaitSeconds
$after = Get-Recorded
$grew = $after - $before
$ratio = if ($downloaded -gt 0) { $grew / $downloaded } else { 0 }
Write-Host ("Recorded growth ('{0}' + unattributed): {1:N0} bytes = {2:P0} of the download" -f $RowKey, $grew, $ratio)

if ($ratio -ge 0.9 -and $ratio -le 1.1) { Write-Host 'PASS' -ForegroundColor Green; exit 0 }
Write-Host 'FAIL: outside 90%..110%. Re-run on a quiet connection; if it still fails, send the usage.json and the log.' -ForegroundColor Red
exit 1
