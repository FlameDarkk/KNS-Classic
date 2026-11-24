# Local KNS Classic backend on 127.0.0.1:8081 with a fresh, throwaway SQLite database (for previews/tests).
# The server keeps its DB at <cwd>\work\kns.sqlite, so it runs from a new folder under work\disposable-backend\.
# Build first:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dotnet.ps1 build server-dotnet\src\KnS.Server -o work\server-build
# -Persistent keeps one database in work\test-backend across runs.
# -WebRoot/-PublicBase run it as a packaged host would (KNS_WEB_ROOT, KNS_PUBLIC_BASE): e.g. -WebRoot build\kns-server\web
# -PublicBase /games/kns/play/ serves the game there and the API under .../api/.
param([int]$Port = 8081, [switch]$Persistent, [string]$WebRoot = '', [string]$PublicBase = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dll = Get-ChildItem (Join-Path $root 'work\server-build') -Recurse -Filter KnS.Server.dll -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { $dll = Get-Item (Join-Path $root 'server-dotnet\src\KnS.Server\bin\Debug\net8.0\KnS.Server.dll') -ErrorAction SilentlyContinue }
if (-not $dll) { Write-Error 'No built KnS.Server.dll. Run scripts\dotnet.ps1 build server-dotnet\src\KnS.Server -o work\server-build first.'; exit 1 }
$run = if ($Persistent) { Join-Path $root 'work\test-backend' } else { Join-Path $root ("work\disposable-backend\" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force (Join-Path $run 'work') | Out-Null
& (Join-Path $PSScriptRoot 'prepare-local-server.ps1')
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$dotnetExe = $null
foreach ($candidate in @(
    $(if ($env:KNS_DOTNET_ROOT) { Join-Path $env:KNS_DOTNET_ROOT 'dotnet.exe' }),
    (Join-Path $root 'tools\dotnet8\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
)) {
    if ($candidate -and (Test-Path $candidate)) { $dotnetExe = $candidate; break }
}
if (-not $dotnetExe) { Write-Error 'No dotnet runtime found. Install .NET 8 or set KNS_DOTNET_ROOT.'; exit 1 }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $env:KNS_ADMIN_KEY) { $env:KNS_ADMIN_KEY = 'disposable-admin' }
if ($WebRoot) { $env:KNS_WEB_ROOT = [IO.Path]::GetFullPath((Join-Path $root $WebRoot)) }
if ($PublicBase) { $env:KNS_PUBLIC_BASE = $PublicBase }
Set-Location $run
Write-Host "Disposable KNS backend on http://127.0.0.1:$Port (server: $($dll.FullName), built $($dll.LastWriteTime); db: $run\work\kns.sqlite)"
& $dotnetExe $dll.FullName $Port
