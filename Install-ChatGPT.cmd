@echo off
setlocal
set "SystemDirectory=System32"
if defined PROCESSOR_ARCHITEW6432 set "SystemDirectory=Sysnative"
"%SystemRoot%\%SystemDirectory%\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File "%~dp0Install-ChatGPT.ps1" -Pause
exit /b %ERRORLEVEL%
