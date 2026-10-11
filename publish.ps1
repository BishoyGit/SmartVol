# Builds both SmartVol flavors into publish/:
#   SmartVol.exe           framework-dependent (~2 MB, needs .NET 10 Desktop Runtime)
#   SmartVol-portable.exe  self-contained (~165 MB, runs on any Windows 10/11 PC)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "SmartVol\SmartVol.csproj"
$publishDir = Join-Path $root "publish"

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Output "Building framework-dependent SmartVol.exe ..."
dotnet publish $proj -c Release --self-contained false `
  -p:PublishSingleFile=true -o (Join-Path $publishDir "fd-tmp") --nologo
if (-not $?) { throw "Framework-dependent publish failed" }

Write-Output "Building self-contained SmartVol-portable.exe ..."
dotnet publish $proj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $publishDir "sc-tmp") --nologo
if (-not $?) { throw "Self-contained publish failed" }

Copy-Item -LiteralPath (Join-Path $publishDir "fd-tmp\SmartVol.exe") -Destination (Join-Path $publishDir "SmartVol.exe") -Force
Copy-Item -LiteralPath (Join-Path $publishDir "sc-tmp\SmartVol.exe") -Destination (Join-Path $publishDir "SmartVol-portable.exe") -Force
Remove-Item -LiteralPath (Join-Path $publishDir "fd-tmp") -Recurse -Force
Remove-Item -LiteralPath (Join-Path $publishDir "sc-tmp") -Recurse -Force
Get-ChildItem -LiteralPath $publishDir -File |
  Where-Object { $_.Extension -eq ".pdb" -or $_.Name -eq "settings.json" -or $_.Name -like "smartvol.log*" } |
  Remove-Item -Force

Write-Output ""
Write-Output "Done:"
Get-ChildItem -LiteralPath $publishDir -File | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
