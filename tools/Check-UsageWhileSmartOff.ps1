<#
.SYNOPSIS
  Checks usage tracking while Smart routing is OFF: downloads a known number of bytes with curl.exe and compares them with
  what the widget recorded for the curl.exe row in %AppData%\NetRouteWidget\usage.json.
.DESCRIPTION
  Run it with Smart routing OFF and the widget running elevated, on a quiet connection. It only reads usage.json and
  settings.json; it never touches the widget, sing-box or the network configuration.
  PASS = the curl.exe row (phone + LAN) grew by 90%..110% of the downloaded bytes.
#>
param(
    [string]$Url = 'https://speed.cloudflare.com/__down?bytes=20000000',
    [int]$Count = 1,
    [string]$RowKey = 'app:curl.exe',
    [int]$WaitSeconds = 40,
    [string]$LimitRate = '2M'
)

$ErrorActionPreference = 'Stop'
$usageFile = Join-Path $env:APPDATA 'NetRouteWidget\usage.json'
$settingsFile = Join-Path $env:APPDATA 'NetRouteWidget\settings.json'
$today = (Get-Date).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)

function Get-Recorded([string]$Key = $RowKey) {
    if (-not (Test-Path -LiteralPath $usageFile)) { return 0L }
    $json = Get-Content -LiteralPath $usageFile -Raw | ConvertFrom-Json
    $day = $json.days.$today
    if (-not $day) { return 0L }
    $row = $day.rows.$Key
    if (-not $row) { return 0L }
    return [long]$row.phone.up + [long]$row.phone.down + [long]$row.lan.up + [long]$row.lan.down
}

if (-not (Get-Process NetRouteWidget -ErrorAction SilentlyContinue)) { throw 'The widget is not running.' }
if (Test-Path -LiteralPath $settingsFile) {
    $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    if ($settings.SmartRouting.Enabled) { throw 'Smart routing is ON. Turn it off first: this checks the Windows tracing path.' }
}

Write-Host "Waiting for the widget to save the current numbers..."
Start-Sleep -Seconds $WaitSeconds
$before = Get-Recorded
$otherBefore = Get-Recorded 'other'

$downloaded = 0L
for ($i = 1; $i -le $Count; $i++) {
    # Rate-limited on purpose: the trace delivers events about a second late, and a process that has already exited by then
    # cannot be named (its bytes land in "Other"). A download that lasts several seconds avoids a false FAIL on a fast line.
    $bytes = & curl.exe -s -L -o NUL -w '%{size_download}' --max-time 120 --limit-rate $LimitRate $Url
    $downloaded += [long]$bytes
}
Write-Host ("Downloaded {0:N0} bytes in {1} request(s) from {2}" -f $downloaded, $Count, $Url)

Write-Host "Waiting for the widget to save (up to $WaitSeconds s)..."
Start-Sleep -Seconds $WaitSeconds
$after = Get-Recorded
$otherGrew = (Get-Recorded 'other') - $otherBefore
$grew = $after - $before
$ratio = if ($downloaded -gt 0) { $grew / $downloaded } else { 0 }
Write-Host ("Recorded growth of '{0}': {1:N0} bytes = {2:P0} of the download (the 'other' row grew by {3:N0} bytes)" -f $RowKey, $grew, $ratio, $otherGrew)

if ($ratio -ge 0.9 -and $ratio -le 1.1) { Write-Host 'PASS' -ForegroundColor Green; exit 0 }
Write-Host 'FAIL: outside 90%..110%. If the ''other'' row grew instead, the process could not be named in time. Is the widget elevated (see the log for "Usage tracking without Smart routing is unavailable")? If it still fails, send usage.json and the log.' -ForegroundColor Red
exit 1
