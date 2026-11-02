@echo off
rem Windows PowerShell and Windows Forms are included in Windows.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-gui.ps1"
if errorlevel 1 pause
