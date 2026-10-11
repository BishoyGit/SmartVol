# Builds both SmartVol flavors into publish/:
#   SmartVol.exe           framework-dependent (~2 MB, needs .NET 10 Desktop Runtime)
#   SmartVol-portable.exe  self-contained (~165 MB, runs on any Windows 10/11 PC)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "SmartVol\SmartVol.csproj"
$out = Join-Path $root "publish"

New-Item -ItemType Directory -Path $out -Force | Out-Null

Write-Output "Building framework-dependent SmartVol.exe ..."
dotnet publish $proj -c Release --self-contained false `
  -p:PublishSingleFile=true -o (Join-Path $out "fd-tmp") --nologo
if (-not $?) { throw "Framework-dependent publish failed" }

Write-Output "Building self-contained SmartVol-portable.exe ..."
dotnet publish $proj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $out "sc-tmp") --nologo
if (-not $?) { throw "Self-contained publish failed" }

Copy-Item -LiteralPath (Join-Path $out "fd-tmp\SmartVol.exe") -Destination (Join-Path $out "SmartVol.exe") -Force
Copy-Item -LiteralPath (Join-Path $out "sc-tmp\SmartVol.exe") -Destination (Join-Path $out "SmartVol-portable.exe") -Force
Remove-Item -LiteralPath (Join-Path $out "fd-tmp") -Recurse -Force
Remove-Item -LiteralPath (Join-Path $out "sc-tmp") -Recurse -Force
Get-ChildItem -LiteralPath $out -Include "*.pdb", "settings.json", "smartvol.log*" | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Output ""
Write-Output "Done:"
Get-ChildItem -LiteralPath $out | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
