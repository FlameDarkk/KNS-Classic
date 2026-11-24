param(
    # Upload the package (build.bat server output) and run its install.sh on the host in deploy\remote.local.json.
    [switch]$SkipBuild,
    # Prepare the package only; no SSH connection.
    [switch]$PrepareOnly,
    # Reset every player's progress after the new server is healthy (install.sh --reset-progress; database backed up first).
    [switch]$ResetProgress,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ExtraArguments
)
# Settings live in deploy\remote.local.json (ignored by git; copy deploy\remote.example.json):
#   { "host": "user@203.0.113.10", "key": "C:\\Users\\me\\.ssh\\id_ed25519", "public_url": "https://example.com/kns/" }
# The host needs sudo without a password prompt for install.sh, systemd, and the package's runtime (self-contained .NET).
$ErrorActionPreference = 'Stop'
$env:PSModulePath = "$PSHOME\Modules;$env:PSModulePath"
try {
    foreach ($argument in $ExtraArguments) {
        if ($argument -ne '--no-pause') { throw "Unknown argument: $argument" }
    }
    $root = Split-Path $PSScriptRoot -Parent
    function Invoke-Checked([string]$Program, [string[]]$Arguments) {
        & $Program @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Program failed (exit $LASTEXITCODE)." }
    }

    $archive = Join-Path $root 'build\kns-server.tar.gz'
    if (-not $SkipBuild) {
        Write-Host 'Building browser game, Admin Studio, and server package...'
        Invoke-Checked 'python' @((Join-Path $root 'browser\port\build.py'))
        Invoke-Checked 'python' @((Join-Path $root 'tools\build_studio.py'))
        Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'package-server.py'))
        Invoke-Checked 'python' @((Join-Path $root 'browser\browser_tools.py'), 'verify-package', 'build/kns-server')
    }
    if (!(Test-Path $archive)) { throw "Package missing: $archive (run build.bat server)." }
    if ($PrepareOnly) { Write-Host "Prepared $archive. No SSH connection made."; exit 0 }

    $configPath = Join-Path $root 'deploy\remote.local.json'
    if (!(Test-Path $configPath)) { throw "Missing $configPath. Copy deploy\remote.example.json and fill in host, key and public_url." }
    $config = Get-Content -Raw $configPath | ConvertFrom-Json
    foreach ($field in 'host', 'key', 'public_url') {
        if (-not $config.$field) { throw "deploy\remote.local.json needs `"$field`"." }
    }
    $remote = [string]$config.host
    $key = [Environment]::ExpandEnvironmentVariables([string]$config.key)
    $publicUrl = ([string]$config.public_url).TrimEnd('/') + '/'
    $ssh = Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
    $scp = Join-Path $env:WINDIR 'System32\OpenSSH\scp.exe'

    foreach ($path in @($ssh, $scp, $key)) {
        if (!(Test-Path $path)) { throw "Required SSH file missing: $path" }
    }
    $options = @('-i', $key, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'ConnectTimeout=15')
    $id = [Guid]::NewGuid().ToString('N')
    $remoteStage = "kns-stage-$id"
    Write-Host "Uploading to $remote..."
    Invoke-Checked $ssh ($options + @($remote, "umask 077 && mkdir ~/$remoteStage"))
    Invoke-Checked $scp ($options + @($archive, "${remote}:$remoteStage/"))
    $reset = if ($ResetProgress) { ' --reset-progress' } else { '' }
    if ($reset) { Write-Host 'This deployment resets every player''s progress (backup first).' }
    Invoke-Checked $ssh ($options + @($remote, "cd ~/$remoteStage && tar -xzf kns-server.tar.gz && sudo -n bash kns-server/install.sh$reset; status=`$?; cd ~ && rm -rf ~/$remoteStage; exit `$status"))

    # Public checks through the host's proxy and TLS; these reads create no accounts.
    $page = Invoke-WebRequest -UseBasicParsing -Uri $publicUrl -TimeoutSec 30
    if ($page.StatusCode -ne 200 -or $page.Content -notmatch 'KNS_ELF') { throw "Public check failed: $publicUrl" }
    $local = [IO.File]::ReadAllText((Join-Path $root 'build\kns-server\web\index.html'))
    if ($page.Content -ne $local) { throw 'The public game page differs from the package. Check proxy caching.' }
    $studio = Invoke-WebRequest -UseBasicParsing -Uri ($publicUrl + 'studio') -TimeoutSec 30
    if ($studio.StatusCode -ne 200) { throw 'Public Studio check failed.' }
    $status = $null
    try { $status = [int](Invoke-WebRequest -UseBasicParsing -Uri ($publicUrl + 'api/admin/accounts') -TimeoutSec 30).StatusCode }
    catch { $status = [int]$_.Exception.Response.StatusCode.value__ }
    if ($status -ne 401) { throw "Public API check failed: api/admin/accounts answered $status, expected 401." }
    Write-Host "Deployed; public checks passed: $publicUrl"
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    Write-Host 'Operation failed. Inspect the error and the host journal (journalctl -u kns-server); do not assume deployment succeeded.'
    exit 1
}
