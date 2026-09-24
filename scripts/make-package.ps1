# Publishes the app and assembles a CDN-ready package in package/.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\make-package.ps1 [-Version 0.1.0] [-SkipPublish]
#
# Two things this does deliberately:
#   * publishes into a FRESH staging directory, because publishing into a populated output
#     directory leaves the previous content-hashed assemblies behind (an old
#     BlazorRunner.<hash>.wasm stays next to the new one, and only one is referenced by
#     blazor.boot.json) — ~12 MB of dead weight each time;
#   * copies only the assets a CDN needs, dropping the .br/.gz siblings a .NET publish emits
#     (jsDelivr compresses responses itself). Copying what we want avoids deleting from the
#     publish output, which can trip over transient file locks.

param(
    [string]$Version = "0.1.0",
    [switch]$SkipPublish,
    [string]$DotnetRoot = (Join-Path $env:USERPROFILE ".dotnet")
)

$ErrorActionPreference = "Stop"

$root = Join-Path $PSScriptRoot ".."
$project = Join-Path $root "src\BlazorRunner"
$package = Join-Path $root "package"
$dist = Join-Path $project "dist"
$staging = $null

if (-not $SkipPublish) {
    $staging = Join-Path $project ("obj\package-staging-" + [guid]::NewGuid().ToString("N").Substring(0, 8))

    & (Join-Path $DotnetRoot "dotnet.exe") publish $project -c Release -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    $wwwroot = Join-Path $staging "wwwroot"
}
else {
    $wwwroot = Join-Path $dist "wwwroot"
}

if (-not (Test-Path $wwwroot)) { throw "No publish output at $wwwroot" }

$frameworks = Get-ChildItem (Join-Path $wwwroot "_framework") -File -Filter "BlazorRunner.*.wasm"
if ($frameworks.Count -ne 1) {
    throw "Publish output has $($frameworks.Count) BlazorRunner assemblies; expected 1."
}

# /E  = include subdirectories (and empty ones)
# /PURGE = remove destination files that are no longer in the source, so a stale package
#          (e.g. from an older layout) cannot linger
# /XF = exclude the pre-compressed siblings
# robocopy exit codes below 8 mean success (1 = files copied, 2 = extras removed, ...).
robocopy $wwwroot $package /E /PURGE /XF *.br *.gz /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

# The layout matters: Blazor resolves its assets relative to the page. Assert the entry points
# survived rather than shipping a package that silently cannot boot.
foreach ($required in @("index.html", "_framework\blazor.webassembly.js", "_framework\dotnet.js")) {
    if (-not (Test-Path (Join-Path $package $required))) { throw "Package is missing $required" }
}

# The compiler's bulk payloads travel beside the app rather than inside it. Embedded, they put
# ~16 MB into the app assembly, which the browser must download before the app can start; as
# payloads the runtime boots against a small assembly and fetches these on first use, and the
# browser caches them across rebuilds of the app. See Payload.cs.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-Payload([string]$zipPath, [string[]]$files) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            # CreateEntryFromFile throws if a source is missing, which is what we want here.
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file,
                [System.IO.Path]::GetFileName($file),
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Host "  $([System.IO.Path]::GetFileName($zipPath)): $($files.Count) entries"
}

New-Payload (Join-Path $package "refs.zip") @(
    (Get-ChildItem (Join-Path $project "refs") -File -Filter *.dll | ForEach-Object FullName)
)

# The Razor compiler is not a package: it ships inside the SDK, so ask MSBuild which SDK the build
# just used rather than guessing at the version directory.
$sdkPath = (& (Join-Path $DotnetRoot "dotnet.exe") msbuild $project -nologo -getProperty:MSBuildSDKsPath |
    Select-Object -Last 1).Trim()
$generators = Join-Path $sdkPath "Microsoft.NET.Sdk.Razor\source-generators"

New-Payload (Join-Path $package "razor.zip") @(
    (Join-Path $generators "Microsoft.CodeAnalysis.Razor.Compiler.dll"),
    (Join-Path $generators "Microsoft.AspNetCore.Razor.Utilities.Shared.dll")
)

foreach ($required in @("refs.zip", "razor.zip")) {
    if (-not (Test-Path (Join-Path $package $required))) { throw "Package is missing $required" }
}

@{
    name = "@live-codes/blazor-wasm"
    version = $Version
    description = "Run C# and render Blazor components in the browser, with no server."
    license = "MIT"
    repository = @{ type = "git"; url = "https://github.com/live-codes/browser-blazor" }
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $package "package.json")

$files = Get-ChildItem $package -Recurse -File
$dropped = (Get-ChildItem $wwwroot -Recurse -File | Where-Object { $_.Extension -in @(".br", ".gz") }).Count
$size = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2)

if ($staging -and (Test-Path $staging)) {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Packaged $($files.Count) files ($size MB) in $package"
Write-Host "  dropped $dropped pre-compressed files"

