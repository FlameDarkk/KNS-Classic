@echo off
rem Unified local launcher for KNS-Classic:
rem 1. Prepares adminkey.txt (auto-generated 64-hex key, never printed).
rem 2. Stops any old verified KNS backend instances and starts the local backend on 127.0.0.1:8081.
rem 3. Builds browser\kns.html (if missing) and browser\kns-studio.html.
rem 4. Starts (or reuses) the static server on 127.0.0.1:8790 and opens both kns.html and kns-studio.html.
setlocal
cd /d "%~dp0"
if /i "%~1"=="--no-pause" set "NOPAUSE=1"

set "PY=python"
where python >nul 2>nul || set "PY=py -3"

if not exist "browser\kns.html" (
  echo browser\kns.html not built yet, building it...
  %PY% browser\port\build.py
  if errorlevel 1 (
    echo Build failed.
    if not defined NOPAUSE pause
    exit /b 1
  )
)

echo Building Admin Studio ...
%PY% tools\build_studio.py
if errorlevel 1 (
  echo Studio build failed. No backend was stopped.
  if not defined NOPAUSE pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\ensure-admin-env.ps1"
if errorlevel 1 (
  echo Could not prepare the admin key file. See the error above.
  if not defined NOPAUSE pause
  exit /b 1
)

for /f "usebackq tokens=1,* delims==" %%a in ("%~dp0adminkey.txt") do (
  if "%%a"=="KNS_ADMIN_KEY" set "KNS_ADMIN_KEY=%%b"
)
if not defined KNS_ADMIN_KEY (
  echo adminkey.txt has no KNS_ADMIN_KEY line.
  if not defined NOPAUSE pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\prepare-local-server.ps1"
if errorlevel 1 (
  echo Could not stop the previous local server. See the error above.
  if not defined NOPAUSE pause
  exit /b 1
)

echo Starting KNS backend on http://127.0.0.1:8081 ...
start "KNS backend (8081)" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\dotnet.ps1" run --project server-dotnet\src\KnS.Server -- 8081

set /a WAIT=0
:waitloop
powershell.exe -NoProfile -Command "try { $r = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:8081/health' -TimeoutSec 2; if ($r.StatusCode -eq 200) { exit 0 } else { exit 1 } } catch { exit 1 }"
if not errorlevel 1 goto ready
set /a WAIT+=1
if %WAIT% GEQ 60 goto timeout
timeout /t 1 /nobreak >nul
goto waitloop
:timeout
echo Backend did not become ready on http://127.0.0.1:8081 in time. Check the KNS backend window.
if not defined NOPAUSE pause
exit /b 1
:ready

powershell.exe -NoProfile -Command "if (Get-NetTCPConnection -LocalPort 8790 -State Listen -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"
if not errorlevel 1 goto reuse

start "" powershell -NoProfile -WindowStyle Hidden -Command "Start-Sleep -Seconds 3; Start-Process 'http://127.0.0.1:8790/kns.html'; Start-Process ('http://127.0.0.1:8790/kns-studio.html#key=' + $env:KNS_ADMIN_KEY)"
echo Game:   http://127.0.0.1:8790/kns.html
echo Studio: http://127.0.0.1:8790/kns-studio.html  (Ctrl+C to stop)
call %PY% browser\serve.py 8790 8081
if not defined NOPAUSE pause
exit /b 0

:reuse
echo Static server already listening on 8790; reusing it.
powershell -NoProfile -WindowStyle Hidden -Command "Start-Process 'http://127.0.0.1:8790/kns.html'; Start-Process ('http://127.0.0.1:8790/kns-studio.html#key=' + $env:KNS_ADMIN_KEY)"
echo Game and Studio open in browser. Close this window when done.
if not defined NOPAUSE pause
exit /b 0
