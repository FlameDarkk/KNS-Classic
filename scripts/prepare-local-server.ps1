$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$serverExe = Join-Path $root 'server-dotnet\src\KnS.Server\bin\Debug\net8.0\KnS.Server.exe'
$serverDll = Join-Path $root 'server-dotnet\src\KnS.Server\bin\Debug\net8.0\KnS.Server.dll'
$project = Join-Path $root 'server-dotnet\src\KnS.Server'

try {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe' OR Name = 'KnS.Server.exe'")
    foreach ($process in $processes) {
        $exe = [string]$process.ExecutablePath
        $command = ([string]$process.CommandLine).Replace('/', '\')
        $isServer = $exe -ieq $serverExe
        $isDll = $command.Contains($serverDll) -or $command.Contains((Join-Path $root 'work\server-build'))
        $isRun = $command -match '\brun\b' -and $command.Contains($project)
        if ($isServer -or $isDll -or $isRun) {
            Write-Host "Stopping previous KNS server process $($process.ProcessId)."
            Stop-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
            Wait-Process -Id $process.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
        }
    }
    $listener = Get-NetTCPConnection -LocalPort 8081 -State Listen -ErrorAction SilentlyContinue
    if ($listener) { throw 'Port 8081 is still in use. The listener was not stopped because its identity was not verified.' }
} catch {
    Write-Error "Local server preparation failed: $_"
    exit 1
}
