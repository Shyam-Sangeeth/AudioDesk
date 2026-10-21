# Builds the AudioDesk installer: publishes a self-contained build into installer\payload, then
# packages it as installer\out\AudioDesk-<version>-x64.msi.
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 1.2.0]
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $root 'AudioDesk.csproj')
    $Version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}

$payload = Join-Path $PSScriptRoot 'payload'
$out = Join-Path $PSScriptRoot 'out'
Remove-Item $payload, $out -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Publishing AudioDesk $Version (self-contained, win-x64)..."
dotnet publish (Join-Path $root 'AudioDesk.csproj') -c Release -r win-x64 --self-contained true -p:Version=$Version -o $payload
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

Write-Host 'Building the installer...'
dotnet build (Join-Path $PSScriptRoot 'AudioDesk.Installer.wixproj') -c Release -p:Version=$Version -p:PayloadDir=$payload -o $out
if ($LASTEXITCODE) { throw 'Installer build failed' }

Get-ChildItem $out -Filter *.msi | Select-Object Name, @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } }, FullName
