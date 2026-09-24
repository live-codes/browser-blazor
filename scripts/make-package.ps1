# Publishes the app and assembles a CDN-ready package in package/.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\make-package.ps1 [-Version 0.1.0] [-SkipPublish]
#
# The .br/.gz siblings that a .NET publish emits are dropped: jsDelivr compresses responses
# itself, so they are dead weight in a package served from a CDN.

param(
    [string]$Version = "0.1.0",
    [switch]$SkipPublish,
    [string]$DotnetRoot = (Join-Path $env:USERPROFILE ".dotnet")
)

$ErrorActionPreference = "Stop"

$root = Join-Path $PSScriptRoot ".."
$project = Join-Path $root "src\BlazorRunner"
$dist = Join-Path $project "dist"
$package = Join-Path $root "package"

if (-not $SkipPublish) {
    # Start from a clean folder. Publishing into a populated output directory leaves the
    # previous content-hashed assemblies behind (an old BlazorRunner.<hash>.wasm stays next to
    # the new one, and only one is referenced by blazor.boot.json) — ~12 MB of dead weight each.
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

    & (Join-Path $DotnetRoot "dotnet.exe") publish $project -c Release -o $dist
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

$wwwroot = Join-Path $dist "wwwroot"
if (-not (Test-Path $wwwroot)) { throw "No publish output at $wwwroot" }

$stale = Get-ChildItem (Join-Path $wwwroot "_framework") -File -Filter "BlazorRunner.*.wasm"
if ($stale.Count -gt 1) { throw "Publish output has $($stale.Count) BlazorRunner assemblies; expected 1." }

if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item -ItemType Directory -Path $package | Out-Null
Copy-Item (Join-Path $wwwroot "*") $package -Recurse

$compressed = Get-ChildItem $package -Recurse -File -Include *.br, *.gz
$compressed | Remove-Item -Force

@{
    name = "@live-codes/blazor-wasm"
    version = $Version
    description = "Run C# and render Blazor components in the browser, with no server."
    license = "MIT"
    repository = @{ type = "git"; url = "https://github.com/live-codes/browser-blazor" }
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $package "package.json")

$files = Get-ChildItem $package -Recurse -File
$size = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2)
Write-Host "Packaged $($files.Count) files ($size MB) in $package"
Write-Host "  dropped $($compressed.Count) pre-compressed files"
