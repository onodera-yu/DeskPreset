# Builds a self-contained single-file DeskPreset.exe (no .NET install needed) and zips it.
#   ./build.ps1                 -> dist/DeskPreset-v<Version>-win-x64.zip
#   ./build.ps1 -Version 1.2.0  -> overrides the version in Directory.Build.props
param(
    [string]$Version,
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $Version) { $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version }
$out = "dist/publish-$Runtime"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish src/DeskPreset.App -c Release -r $Runtime --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Copy-Item README.md $out
$zip = "dist/DeskPreset-v$Version-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out/*" -DestinationPath $zip
Write-Host "Created $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
