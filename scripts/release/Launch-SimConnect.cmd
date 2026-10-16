@echo off
cd /d "%~dp0"
echo AeroScience MSFS mode. Open http://127.0.0.1:5080 in your browser.
AeroScience.Engine.exe --AeroScience:Telemetry=SimConnect
pause
