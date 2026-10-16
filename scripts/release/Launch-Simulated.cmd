@echo off
cd /d "%~dp0"
echo AeroScience test mode. Open http://127.0.0.1:5080 in your browser.
AeroScience.Engine.exe --AeroScience:Telemetry=Simulated
pause
