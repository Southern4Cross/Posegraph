@echo off
rem Starts the Posegraph backend manually (the plugin can also start it for you).
rem Options: set POSEGRAPH_DEVICE=auto^|cuda^|cpu, POSEGRAPH_RELEASE_AFTER=0 (seconds, -1 = keep loaded), POSEGRAPH_PORT=42430
setlocal
set "ROOT=%~dp0"
set "PYTHONPATH=%ROOT%backend\src"
set "PYTHONIOENCODING=utf-8"
set "PYTHONUNBUFFERED=1"
set "PYTHONNOUSERSITE=1"
if not defined POSEGRAPH_PORT set "POSEGRAPH_PORT=42430"
cd /d "%ROOT%backend"
echo Posegraph backend: http://127.0.0.1:%POSEGRAPH_PORT%  - close this window to stop.
"%ROOT%python\python.exe" -m posegraph.app
pause
