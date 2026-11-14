# Builds dist\NotesSetup.exe: one self-contained installer (the target PC needs no .NET).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$app = Join-Path $PSScriptRoot 'publish\app'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish Notes.csproj -c Release -r win-x64 --self-contained true -o $app
if ($LASTEXITCODE) { throw 'Publishing the app failed' }

$zip = Join-Path $PSScriptRoot 'Installer\payload.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -CompressionLevel Optimal

dotnet publish Installer\Installer.csproj -c Release -o (Join-Path $PSScriptRoot 'dist')
if ($LASTEXITCODE) { throw 'Building the installer failed' }

Get-Item (Join-Path $PSScriptRoot 'dist\NotesSetup.exe') | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
