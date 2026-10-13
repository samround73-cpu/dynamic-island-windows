@echo off
title Restore the Windows taskbar
echo.
echo  This brings the normal Windows taskbar back if Dynamic Island was force-closed
echo  (e.g. from Task Manager) and didn't get the chance to restore it itself.
echo.
echo  It turns taskbar auto-hide OFF and restarts Explorer (your screen will flicker).
echo  If you normally use auto-hide, switch it back on afterwards in
echo  Settings ^> Personalisation ^> Taskbar ^> Taskbar behaviours.
echo.
pause

taskkill /f /im DynamicIsland.exe >nul 2>nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p='HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3';" ^
  "try { $v=(Get-ItemProperty -Path $p -ErrorAction Stop).Settings; $v[8]=2; Set-ItemProperty -Path $p -Name Settings -Value $v } catch {}" ^
  "Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2;" ^
  "if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }"

echo.
echo  Done - your taskbar should be back.
pause
