# Runs the .NET 8 SDK for this repo without touching machine-wide temp/NuGet locks.
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dotnet.ps1 test server-dotnet/tests/KnS.Tests -o work/test-build
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dotnet.ps1 publish server-dotnet/src/KnS.Server -c Release ...
# SDK lookup order:
#   1. $env:KNS_DOTNET_ROOT\dotnet.exe (optional override)
#   2. tools\dotnet8\dotnet.exe (optional repo-local SDK, gitignored)
#   3. dotnet on PATH (standard .NET 8 SDK install)
$DotnetArgs = [string[]]$args
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$DotnetArgs -or $DotnetArgs.Count -eq 0) { throw 'Usage: scripts\dotnet.ps1 <dotnet command> [args]' }

$sdkRoot = $null
if ($env:KNS_DOTNET_ROOT -and (Test-Path (Join-Path $env:KNS_DOTNET_ROOT 'dotnet.exe'))) {
    $sdkRoot = $env:KNS_DOTNET_ROOT
} elseif (Test-Path (Join-Path $root 'tools\dotnet8\dotnet.exe')) {
    $sdkRoot = Join-Path $root 'tools\dotnet8'
} else {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $sdkRoot = Split-Path $cmd.Source }
}
if (!$sdkRoot) { throw 'No .NET 8 SDK found. Install the .NET 8 SDK on PATH or set KNS_DOTNET_ROOT.' }
$dotnet = Join-Path $sdkRoot 'dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = (Get-Command dotnet).Source }

$temp = Join-Path $root 'work\dotnet-tmp'
foreach ($dir in @($temp, (Join-Path $temp 'NuGetScratch'), (Join-Path $temp 'http-cache'))) { New-Item -ItemType Directory -Force $dir | Out-Null }
$env:TEMP = $temp; $env:TMP = $temp
$env:NUGET_SCRATCH = Join-Path $temp 'NuGetScratch'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $temp 'http-cache'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:DOTNET_ROOT = $sdkRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBuildSDKsPath = $null
$noServers = @('-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false')

$sdkDll = Get-ChildItem -Path (Join-Path $sdkRoot 'sdk\*\dotnet.dll') -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1
function Test-DotnetHost([string]$Program, [string[]]$Prefix) {
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $null = & $Program @($Prefix + '--version') 2>&1; return ($LASTEXITCODE -eq 0) }
    catch { return $false } finally { $ErrorActionPreference = $previous }
}
$dotnetHost = $dotnet; $dotnetPrefix = @()
if (!(Test-DotnetHost $dotnet @())) {
    if (!$sdkDll) { throw "Cannot start .NET host at $dotnet and no sdk\*\dotnet.dll found under $sdkRoot." }
    $candidates = @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) + @(Get-Command dotnet.exe -All -ErrorAction SilentlyContinue | ForEach-Object Source)
    $fallback = $candidates | Where-Object { $_ -and (Test-Path $_) -and ($_ -ne $dotnet) } | Select-Object -Unique |
        Where-Object { Test-DotnetHost $_ @($sdkDll.FullName) } | Select-Object -First 1
    if (!$fallback) { throw "Cannot start .NET host and no installed .NET 8+ host can run $($sdkDll.FullName)." }
    Write-Host "Using $fallback with SDK at $sdkRoot."
    $dotnetHost = $fallback; $dotnetPrefix = @($sdkDll.FullName)
    $env:DOTNET_ROOT = Split-Path $fallback; $env:DOTNET_HOST_PATH = $fallback
    $env:MSBuildSDKsPath = Join-Path $sdkDll.DirectoryName 'Sdks'
}

function Invoke-Sdk([string[]]$Arguments) {
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $dotnetHost @($dotnetPrefix + $Arguments) 2>&1 | ForEach-Object { "$_" } | Out-Host; return $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
}

# Restore once per change of any project/props file (content hash), then always build with --no-restore.
$inputs = @(Get-ChildItem (Join-Path $root 'server-dotnet') -Recurse -Include *.csproj, Directory.Build.props, Directory.Packages.props -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object FullName)
$hash = ($inputs | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash + $_.FullName }) -join "`n"
$hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($hash))).Replace('-', '')
$stamp = Join-Path $root 'work\dotnet-restore.stamp'
$assetsMissing = @($inputs | Where-Object { $_.Extension -eq '.csproj' } |
    Where-Object { !(Test-Path (Join-Path $_.DirectoryName 'obj\project.assets.json')) }).Count -gt 0
$command = $DotnetArgs[0]
$needsRestore = $command -in @('build', 'test', 'publish', 'run', 'restore')
if ($needsRestore -and ($assetsMissing -or !(Test-Path $stamp) -or (Get-Content $stamp -Raw).Trim() -ne $hash)) {
    Write-Host 'Project files changed since the last restore: restoring server-dotnet once...'
    foreach ($project in @('server-dotnet\src\KnS.Server', 'server-dotnet\tests\KnS.Tests')) {
        $code = Invoke-Sdk (@('restore', (Join-Path $root $project)) + $noServers)
        if ($code -ne 0) { exit $code }
    }
    Set-Content -Path $stamp -Value $hash -Encoding ascii
}
if ($command -eq 'restore') { exit 0 }

$objRoot = Join-Path $root 'work\dotnet-obj'
$staleBefore = [DateTime]::Now.AddMinutes(-30)
if ($needsRestore) {
    New-Item -ItemType Directory -Force $objRoot | Out-Null
    Get-ChildItem $objRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt $staleBefore } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    $env:KnsObjRoot = Join-Path $objRoot ([DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + (Get-Random))
}

if ($command -eq 'test') {
    for ($i = 1; $i -lt $DotnetArgs.Count - 1; $i++) {
        if ($DotnetArgs[$i] -in @('-o', '--output')) {
            $base = $DotnetArgs[$i + 1]
            if (![IO.Path]::IsPathRooted($base)) { $base = Join-Path (Get-Location) $base }
            New-Item -ItemType Directory -Force $base | Out-Null
            Get-ChildItem $base -Directory -Filter 'run-*' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt $staleBefore } |
                ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
            $DotnetArgs[$i + 1] = Join-Path $base ('run-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + (Get-Random))
        }
    }
}

$extra = @()
if ($needsRestore) { $extra = @('--no-restore') + $noServers + @('--disable-build-servers') }
if ($command -eq 'run') { $extra = @('--no-restore') }
exit (Invoke-Sdk (@($DotnetArgs[0]) + $extra + @($DotnetArgs | Select-Object -Skip 1)))
