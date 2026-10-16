@echo off
cd /d "%~dp0"
echo AeroScience test mode. Open http://127.0.0.1:5080 in your browser.
AeroScience.Engine.exe --AeroScience:Telemetry=Simulated --AeroScience:EnableToolbarBridge=false --AeroScience:EnableLan=false %*
set "AEROSCIENCE_EXIT=%ERRORLEVEL%"
if not "%AEROSCIENCE_EXIT%"=="0" echo Engine exited with code %AEROSCIENCE_EXIT%. See Collect-Feedback.ps1 and the installation guide.
pause
exit /b %AEROSCIENCE_EXIT%
