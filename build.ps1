# Builds the self-contained exe and the MSI installer into dist\.
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet publish DocsSync.csproj -c Release -r win-x64 -o publish -p:Version=$Version
if ($LASTEXITCODE) { exit $LASTEXITCODE }

& .\publish\DocsSync.exe --check publish\check.txt
if ($LASTEXITCODE) { Get-Content publish\check.txt | Select-String FAIL; throw "Self-check failed" }

dotnet build installer\DocsSync.Installer.wixproj -c Release -p:Version=$Version
if ($LASTEXITCODE) { exit $LASTEXITCODE }

New-Item -ItemType Directory dist -Force | Out-Null
Copy-Item installer\bin\x64\Release\DocsSync-Setup.msi dist\DocsSync-Setup-$Version.msi -Force
Write-Host "Built dist\DocsSync-Setup-$Version.msi"
