# Pack installer payload into Payload.zip
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot   # repo root (scripts/..)
$payload = Join-Path $root 'src\AppClone.Setup\Payload'
$stage = Join-Path $payload 'stage'

if (-not (Test-Path (Join-Path $root 'dist\AppClone.exe'))) {
    throw "dist\AppClone.exe not found - publish the main app first"
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item (Join-Path $root 'dist\AppClone.exe') $stage
Copy-Item (Join-Path $root 'dist\sandbox') -Destination $stage -Recurse

$zipPath = Join-Path $payload 'payload.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force

Remove-Item $stage -Recurse -Force
$mb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "payload.zip packed ($mb MB)"
