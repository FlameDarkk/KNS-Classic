@echo off
rem Remote deployment launcher: builds the server package (build.bat server) and installs it on the host named in
rem deploy\remote.local.json (copy deploy\remote.example.json) with its install.sh. Game, Studio and API ship together.
rem   deploy.bat [--no-pause] [-PrepareOnly] [-SkipBuild] [-ResetProgress]
rem -PrepareOnly: build the package, no SSH. -SkipBuild: upload the existing build\kns-server.tar.gz.
rem -ResetProgress: after the new server is healthy, reset every player's progress (tools/reset_progress.py, backup first).
rem Accounts, logins and usernames stay. Never implied; pass it explicitly.
setlocal EnableDelayedExpansion
cd /d "%~dp0"
set "PS_ARGS="
set "NOPAUSE="

:parse_args
if "%~1"=="" goto :run_deploy
if /i "%~1"=="--no-pause" ( set "NOPAUSE=1" & set "PS_ARGS=!PS_ARGS! --no-pause" & shift & goto :parse_args )
if /i "%~1"=="-PrepareOnly" ( set "PS_ARGS=!PS_ARGS! -PrepareOnly" & shift & goto :parse_args )
if /i "%~1"=="-SkipBuild" ( set "PS_ARGS=!PS_ARGS! -SkipBuild" & shift & goto :parse_args )
if /i "%~1"=="-ResetProgress" ( set "PS_ARGS=!PS_ARGS! -ResetProgress" & shift & goto :parse_args )
echo Unknown argument: %~1
echo Usage: deploy.bat [--no-pause] [-PrepareOnly] [-SkipBuild] [-ResetProgress]
if not defined NOPAUSE pause
exit /b 1

:run_deploy
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\deploy-remote.ps1" %PS_ARGS%
set "result=%errorlevel%"
if not defined NOPAUSE pause
exit /b %result%
