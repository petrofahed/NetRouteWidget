@echo off
setlocal
rem NetRoute Widget break-glass restore.
rem Hands every network adapter back to Windows' automatic metric, stops the widget (and sing-box), sets its
rem saved mode to Auto and switches Smart routing off, so nothing is re-applied at the next logon. Works without the app.
rem   Restore-Network.cmd          restore (asks for administrator rights)
rem   Restore-Network.cmd /check   dry run: show what would change, change nothing

if /i "%~1"=="/check" goto check

rem fltmc needs administrator rights and does not depend on the Server service (unlike net session).
fltmc >nul 2>&1
if not errorlevel 1 goto admin
if /i "%~1"=="/elevated" (
    echo Could not get administrator rights.
    pause
    exit /b 1
)
echo Requesting administrator rights...
rem The path goes through an environment variable so quotes in it cannot break the PowerShell command.
set "NRW_SELF=%~f0"
powershell -NoProfile -Command "try { Start-Process -FilePath $env:NRW_SELF -ArgumentList '/elevated' -Verb RunAs -ErrorAction Stop } catch { exit 1 }"
if errorlevel 1 (
    echo Administrator rights are required - nothing was changed.
    pause
    exit /b 1
)
exit /b 0

:admin
echo Stopping NetRoute Widget...
taskkill /IM NetRouteWidget.exe /F >nul 2>&1
echo Stopping sing-box (Smart routing)...
taskkill /IM sing-box.exe /F >nul 2>&1

echo Restoring automatic interface metrics...
echo Note: this resets ALL adapters with a manual metric, including ones not set by NetRoute Widget.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$n = 0; $f = 0; Get-NetIPInterface | Where-Object AutomaticMetric -eq 'Disabled' | ForEach-Object { $i = $_; try { Set-NetIPInterface -InterfaceIndex $i.ifIndex -AddressFamily $i.AddressFamily -AutomaticMetric Enabled -ErrorAction Stop; Write-Host ('  restored ' + $i.InterfaceAlias + ' (' + $i.AddressFamily + ')'); $n++ } catch { Write-Host ('  FAILED ' + $i.InterfaceAlias + ' (' + $i.AddressFamily + '): ' + $_.Exception.Message); $f++ } }; if ($n -eq 0 -and $f -eq 0) { Write-Host '  nothing to restore' }; if ($n -gt 0) { Write-Host ('  restored ' + $n) }; if ($f -gt 0) { Write-Host ('  ' + $f + ' failed') }"

echo Setting the widget's saved mode to Auto and switching Smart routing off...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = Join-Path $env:APPDATA 'NetRouteWidget\settings.json'; Write-Host ('  settings file: ' + $p); if (-not (Test-Path $p)) { Write-Host '  no settings file'; exit 0 }; try { $s = Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop; if ($null -eq $s -or -not ($s.PSObject.Properties.Name -contains 'Mode')) { throw 'no Mode in settings file' }; $s.Mode = 'Auto'; if ($s.PSObject.Properties.Name -contains 'SmartRouting' -and $null -ne $s.SmartRouting -and $s.SmartRouting.PSObject.Properties.Name -contains 'Enabled') { $s.SmartRouting.Enabled = $false }; $s | ConvertTo-Json -Depth 10 | Set-Content $p -Encoding UTF8; Write-Host '  saved mode is now Auto, Smart routing is off' } catch { try { @{ Mode = 'Auto' } | ConvertTo-Json -Compress | Set-Content $p -Encoding UTF8; Write-Host '  settings file was unreadable; replaced with Mode=Auto' } catch { Write-Host ('  could not update settings: ' + $_.Exception.Message) } }"

echo.
choice /C YN /N /M "Also stop NetRoute Widget from starting with Windows? [Y/N] "
if errorlevel 2 goto done
schtasks /Delete /TN NetRouteWidget /F

:done
echo.
echo Done. Internet should work again within a few seconds.
pause
exit /b 0

:check
echo Dry run - nothing will be changed.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$list = @(Get-NetIPInterface | Where-Object AutomaticMetric -eq 'Disabled'); if ($list.Count -eq 0) { Write-Host '  nothing to restore' } else { $list | ForEach-Object { Write-Host ('  would restore ' + $_.InterfaceAlias + ' (' + $_.AddressFamily + ', metric ' + $_.InterfaceMetric + ')') } }; Write-Host ('  settings file: ' + (Join-Path $env:APPDATA 'NetRouteWidget\settings.json'))"
schtasks /Query /TN NetRouteWidget >nul 2>&1 && (echo   startup task: present) || (echo   startup task: not present)
tasklist /FI "IMAGENAME eq sing-box.exe" 2>nul | find /I "sing-box.exe" >nul && (echo   sing-box: running) || (echo   sing-box: not running)
rem A leftover TUN adapter (sing-box killed uncleanly) can still capture traffic; it is only reported here, never touched.
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Get-NetAdapter -Name NetRoute -ErrorAction SilentlyContinue) { Write-Host '  NetRoute adapter: present' } else { Write-Host '  NetRoute adapter: absent' }"
exit /b 0
