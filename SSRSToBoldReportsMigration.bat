@echo off

echo Starting Migration...

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "SSRSToBoldReportsMigration.ps1"

echo.
echo Migration Completed

pause