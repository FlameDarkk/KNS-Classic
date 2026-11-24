@echo off
rem Unified build launcher for KNS-Classic.
rem Usage:
rem   build.bat [browser|studio|server|android|android-legacy|android-modern|android-64|all-android] [--no-pause] [--out PATH] [--rid RID]
rem Or double-click to select from the interactive menu.
rem browser:        browser\kns.html from browser\port + apk-cpu-engine + browser\kns_data. Native APK targets run it first.
rem android-legacy: build\KnS-Android-Legacy.apk, native, target SDK 24 (android\ source + browser build).
rem android-modern: build\KnS-Android-Modern.apk, native, target SDK 34 (same package and key as legacy).
rem android-64:     build\KnS-Android64.apk, native arm64-v8a (libgame.so translated to C, android64\); 64-bit-only phones.
rem studio:         browser\kns-studio.html and its icon bundle (tools\build_studio.py).
rem server:         build\kns-server\, the portable server package: self-contained Linux server + web root (deploy\README.md).
rem android: legacy + modern. all-android: legacy + modern + 64. --out applies to legacy and modern only.
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "ACTION="
set "NOPAUSE="
set "EXTRA="

:parse_args
if "%~1"=="" goto :args_done
if /i "%~1"=="browser" ( set "ACTION=Browser" & shift & goto :parse_args )
if /i "%~1"=="android" ( set "ACTION=Android" & shift & goto :parse_args )
if /i "%~1"=="android-legacy" ( set "ACTION=Legacy" & shift & goto :parse_args )
if /i "%~1"=="android-modern" ( set "ACTION=Modern" & shift & goto :parse_args )
if /i "%~1"=="android-64" ( set "ACTION=Android64" & shift & goto :parse_args )
if /i "%~1"=="studio" ( set "ACTION=Studio" & shift & goto :parse_args )
if /i "%~1"=="server" ( set "ACTION=Server" & shift & goto :parse_args )
if /i "%~1"=="all-android" ( set "ACTION=AllAndroid" & shift & goto :parse_args )
if /i "%~1"=="--no-pause" ( set "NOPAUSE=1" & shift & goto :parse_args )
if /i "%~1"=="--out" ( set "EXTRA=!EXTRA! --out "%~2"" & shift & shift & goto :parse_args )
if /i "%~1"=="--rid" ( set "EXTRA=!EXTRA! --rid "%~2"" & shift & shift & goto :parse_args )
echo Unknown argument: %~1
echo Usage: build.bat [browser^|studio^|server^|android^|android-legacy^|android-modern^|android-64^|all-android] [--no-pause] [--out PATH] [--rid RID]
if not defined NOPAUSE pause
exit /b 1

:args_done
if defined ACTION goto :run_build

echo ===================================================
echo KNS-CLASSIC BUILD
echo ===================================================
echo   [1] Android native legacy + modern  (build\KnS-Android-Legacy.apk, build\KnS-Android-Modern.apk)
echo   [2] Android native legacy only      (target SDK 24)
echo   [3] Android native modern only      (target SDK 34)
echo   [4] Android native 64-bit only      (build\KnS-Android64.apk, arm64-v8a, 64-bit-only phones)
echo   [5] Portable server package         (build\kns-server\)
echo   [6] All three Android APKs
echo   [7] Browser only                    (browser\kns.html)
echo   [8] Studio                          (browser\kns-studio.html)
echo   [Q] Cancel
echo ===================================================
set /p "CHOICE=Select target [1-8 or Q]: "
if /i "%CHOICE%"=="1" set "ACTION=Android"
if /i "%CHOICE%"=="2" set "ACTION=Legacy"
if /i "%CHOICE%"=="3" set "ACTION=Modern"
if /i "%CHOICE%"=="4" set "ACTION=Android64"
if /i "%CHOICE%"=="5" set "ACTION=Server"
if /i "%CHOICE%"=="6" set "ACTION=AllAndroid"
if /i "%CHOICE%"=="7" set "ACTION=Browser"
if /i "%CHOICE%"=="8" set "ACTION=Studio"
if /i "%CHOICE%"=="android" set "ACTION=Android"
if /i "%CHOICE%"=="q" exit /b 0
if not defined ACTION (
  echo Invalid selection: %CHOICE%
  if not defined NOPAUSE pause
  exit /b 1
)

:run_build
set "PY=python"
where python >nul 2>nul || set "PY=py -3"

set "DO_LEGACY="
set "DO_MODERN="
set "DO_64="
set "DO_BROWSER="
if /i "%ACTION%"=="Android" ( set "DO_LEGACY=1" & set "DO_MODERN=1" & set "EXTRA=" )
if /i "%ACTION%"=="AllAndroid" ( set "DO_LEGACY=1" & set "DO_MODERN=1" & set "DO_64=1" & set "EXTRA=" )
if /i "%ACTION%"=="Legacy" set "DO_LEGACY=1"
if /i "%ACTION%"=="Modern" set "DO_MODERN=1"
if /i "%ACTION%"=="Android64" set "DO_64=1"

if /i "%ACTION%"=="Browser" set "DO_BROWSER=1"
if /i "%ACTION%"=="Server" set "DO_BROWSER=1"
if defined DO_LEGACY set "DO_BROWSER=1"
if defined DO_MODERN set "DO_BROWSER=1"
if defined DO_64 set "DO_BROWSER=1"

if defined DO_BROWSER (
  echo [build] browser\kns.html from browser\port + apk-cpu-engine + browser\kns_data
  %PY% browser\port\build.py
  if errorlevel 1 goto :failed
)
if /i "%ACTION%"=="Studio" (
  echo [build] browser\kns-studio.html from browser\studio
  %PY% tools\build_studio.py
  if errorlevel 1 goto :failed
)
if /i "%ACTION%"=="Server" (
  echo [build] browser\kns-studio.html from browser\studio
  %PY% tools\build_studio.py
  if errorlevel 1 goto :failed
  echo [build] build\kns-server\ portable server package
  %PY% scripts\package-server.py!EXTRA!
  if errorlevel 1 goto :failed
)
if defined DO_LEGACY (
  echo [build] Android legacy APK from android\ source + browser\kns.html + browser\kns_data
  %PY% android\build_android.py --variant legacy!EXTRA!
  if errorlevel 1 goto :failed
)
if defined DO_MODERN (
  echo [build] Android modern APK from android\ source + browser\kns.html + browser\kns_data
  %PY% android\build_android.py --variant modern!EXTRA!
  if errorlevel 1 goto :failed
)
if defined DO_64 (
  echo [build] Android 64-bit APK from browser\kns.html ELF translated to C + android\ source
  %PY% android64\build_android64.py translate
  if errorlevel 1 goto :failed
  %PY% android64\build_android64.py apk
  if errorlevel 1 goto :failed
)
echo [build] %ACTION% done.
if not defined NOPAUSE pause
exit /b 0

:failed
echo [build] %ACTION% FAILED.
if not defined NOPAUSE pause
exit /b 1
