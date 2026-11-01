# Builds ClipVault and installs it so it replaces the Windows clipboard panel (Win+V).
#   powershell -ExecutionPolicy Bypass -File install.ps1
# Per-user install, no admin rights needed. Undo with uninstall.ps1.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\ClipVault'

# 1. Stop any running copy first: it locks bin\ClipVault.exe and the installed exe.
Get-Process ClipVault -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 2. Build (uses the C# compiler that ships with Windows; no SDK needed).
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

# 3. Copy to a permanent location (not the repo, so rebuilding/deleting the repo doesn't break it).
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $root 'bin\ClipVault.exe') (Join-Path $dest 'ClipVault.exe') -Force

# 4. Start it. On launch ClipVault registers itself under HKCU\...\Run (Start with Windows)
#    and takes over Win+V with a keyboard hook. No Windows settings are changed.
Start-Process (Join-Path $dest 'ClipVault.exe')
Start-Sleep -Seconds 2

$running = Get-Process ClipVault -ErrorAction SilentlyContinue
$run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).ClipVault
if (-not $running) { throw 'ClipVault did not start. See %LOCALAPPDATA%\ClipVault\log.txt' }
Write-Host "Installed to $dest" -ForegroundColor Green
Write-Host "Running (pid $($running.Id)). Start with Windows: $run"
Write-Host 'Press Win+V to open ClipVault.'
