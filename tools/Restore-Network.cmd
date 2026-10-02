@echo off
setlocal
rem NetRoute Widget break-glass restore.
rem Hands every network adapter back to Windows' automatic metric, stops the widget and sets its
rem saved mode to Auto, so it does not re-apply a preference at the next logon. Works without the app.
rem   Restore-Network.cmd          restore (asks for administrator rights)
rem   Restore-Network.cmd /check   dry run: show what would change, change nothing

if /i "%~1"=="/check" goto check

net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b 0
)

echo Stopping NetRoute Widget...
taskkill /IM NetRouteWidget.exe /F >nul 2>&1

echo Restoring automatic interface metrics...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$n = 0; Get-NetIPInterface | Where-Object AutomaticMetric -eq 'Disabled' | ForEach-Object { Set-NetIPInterface -InterfaceIndex $_.ifIndex -AddressFamily $_.AddressFamily -AutomaticMetric Enabled; Write-Host ('  restored ' + $_.InterfaceAlias + ' (' + $_.AddressFamily + ')'); $n++ }; if ($n -eq 0) { Write-Host '  nothing to restore' }"

echo Setting the widget's saved mode to Auto...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = Join-Path $env:APPDATA 'NetRouteWidget\settings.json'; if (-not (Test-Path $p)) { Write-Host '  no settings file'; exit 0 }; try { $s = Get-Content $p -Raw | ConvertFrom-Json; $s.Mode = 'Auto'; $s | ConvertTo-Json | Set-Content $p -Encoding UTF8; Write-Host '  saved mode is now Auto' } catch { Write-Host ('  could not update settings: ' + $_.Exception.Message) }"

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
exit /b 0
