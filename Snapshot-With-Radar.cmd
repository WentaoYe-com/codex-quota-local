@echo off
cd /d "%~dp0"
"%~dp0CodexQuotaLocalCli.exe" --snapshot --radar
pause
