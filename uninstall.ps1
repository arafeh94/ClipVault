# Removes ClipVault and gives Win+V back to Windows.
#   powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-KeepHistory]
param([switch]$KeepHistory)
$ErrorActionPreference = 'Stop'

Get-Process ClipVault -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ClipVault -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\ClipVault') -Recurse -Force -ErrorAction SilentlyContinue
if (-not $KeepHistory) { Remove-Item (Join-Path $env:LOCALAPPDATA 'ClipVault') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host 'ClipVault removed. Win+V opens the Windows clipboard panel again.' -ForegroundColor Green
