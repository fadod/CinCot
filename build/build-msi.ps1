# Builds artifacts\Sikirincot-<version>-x64.msi.
#   1. Publishes Sikirincot self-contained (no .NET install needed on the target PC) as one exe.
#   2. Builds the WiX installer around it.
# Usage: powershell -File build\build-msi.ps1   (needs the .NET 8 SDK; WiX comes from NuGet)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish-msi'
$out = Join-Path $root 'artifacts'

# Use a dotnet that has an SDK: the one on PATH may be a runtime-only install, in which case fall
# back to a per-user SDK in %LOCALAPPDATA%\Microsoft\dotnet.
$hasSdk = (Get-Command dotnet -ErrorAction SilentlyContinue) -and (dotnet --list-sdks 2>$null)
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (-not $hasSdk -and (Test-Path (Join-Path $userDotnet 'dotnet.exe'))) {
    $env:DOTNET_ROOT = $userDotnet
    $env:PATH = "$userDotnet;$env:PATH"
}
$env:DOTNET_NOLOGO = '1'

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\Sikirincot\Sikirincot.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

dotnet build (Join-Path $root 'installer\Sikirincot.Installer.wixproj') -c Release "-p:PublishDir=$publish\" -o (Join-Path $out 'msi')
if ($LASTEXITCODE -ne 0) { throw 'WiX build failed' }

$version = (Get-Item (Join-Path $publish 'Sikirincot.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
$msi = Join-Path $out "Sikirincot-$version-x64.msi"
Move-Item (Join-Path $out 'msi\Sikirincot.msi') $msi -Force
Remove-Item (Join-Path $out 'msi') -Recurse -Force
"Built $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
