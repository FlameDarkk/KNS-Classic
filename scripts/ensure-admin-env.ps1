$ErrorActionPreference = 'Stop'
# Ensures adminkey.txt holds a random KNS_ADMIN_KEY (hex, 64 chars).
# The key value is never printed; startgame.bat loads it via for /f.
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$envFile = Join-Path $root 'adminkey.txt'

if (Test-Path $envFile) {
    $line = Get-Content $envFile -ErrorAction SilentlyContinue |
        Where-Object { $_ -like 'KNS_ADMIN_KEY=*' } | Select-Object -First 1
    if ($line) {
        $value = $line.Substring('KNS_ADMIN_KEY='.Length).Trim()
        if ($value.Length -ge 16) {
            Write-Host 'Admin key already present in adminkey.txt.'
            exit 0
        }
    }
}

New-Item -ItemType Directory -Path (Split-Path $envFile) -Force | Out-Null
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $bytes = New-Object byte[] 32
    $rng.GetBytes($bytes)
} finally { $rng.Dispose() }
$hex = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
[IO.File]::WriteAllText($envFile, "KNS_ADMIN_KEY=$hex`n", (New-Object Text.UTF8Encoding($false)))
Write-Host 'Generated adminkey.txt with a new admin key.'
