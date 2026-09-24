# Copies the reference assemblies Roslyn needs to compile user components:
#   - Microsoft.NETCore.App.Ref                       (the BCL)
#   - Microsoft.AspNetCore.App.Ref                    (Blazor: Microsoft.AspNetCore.Components*, ...)
#   - Microsoft.AspNetCore.Components.WebAssembly     (the Blazor WebAssembly assemblies, which the
#                                                      ASP.NET Core ref pack does not contain)
# into src/BlazorRunner/refs, where they are embedded into the app assembly. Re-run after upgrading
# the .NET SDK.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\prepare-refs.ps1 [-SdkRoot <dotnet root>]

param(
    [string]$SdkRoot = (Join-Path $env:USERPROFILE ".dotnet")
)

$ErrorActionPreference = "Stop"

function Get-LatestRefDir([string]$PackName) {
    $packRoot = Join-Path $SdkRoot "packs\$PackName"
    if (-not (Test-Path $packRoot)) { throw "$PackName pack not found under $packRoot" }
    $packDir = Get-ChildItem $packRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
    $refDir = Get-ChildItem (Join-Path $packDir.FullName "ref") -Directory |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $refDir) { throw "Ref pack has no target-framework folder under $($packDir.FullName)" }
    return $refDir
}

# Copies every DLL that is not already there, so earlier sources win any name overlap.
function Copy-NewDlls([string]$SourceDir, [string]$TargetDir) {
    if (-not (Test-Path $SourceDir)) { return }
    Get-ChildItem (Join-Path $SourceDir "*.dll") | ForEach-Object {
        $destination = Join-Path $TargetDir $_.Name
        if (-not (Test-Path $destination)) { Copy-Item $_.FullName $destination }
    }
}

$netRefDir = Get-LatestRefDir "Microsoft.NETCore.App.Ref"
$aspNetRefDir = Get-LatestRefDir "Microsoft.AspNetCore.App.Ref"

# Blazor must be present or user components cannot compile against it.
$blazorRefs = Get-ChildItem (Join-Path $aspNetRefDir "Microsoft.AspNetCore.Components*.dll")
if (-not $blazorRefs) { throw "Microsoft.AspNetCore.Components*.dll not found in $aspNetRefDir" }

# The Blazor WebAssembly assemblies are not in the ref pack — they ship in this package.
$wasmPackageRoot = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.aspnetcore.components.webassembly"
if (-not (Test-Path $wasmPackageRoot)) {
    throw "microsoft.aspnetcore.components.webassembly not found in the NuGet cache; restore the project first."
}
$wasmVersion = Get-ChildItem $wasmPackageRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
$wasmLibDir = Get-ChildItem (Join-Path $wasmVersion.FullName "lib") -Directory |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName

$target = Join-Path $PSScriptRoot "..\src\BlazorRunner\refs"
New-Item -ItemType Directory -Path $target -Force | Out-Null
Get-ChildItem $target -Filter *.dll | Remove-Item -Force

Copy-NewDlls $netRefDir $target
Copy-NewDlls $aspNetRefDir $target
Copy-NewDlls $wasmLibDir $target

if (-not (Test-Path (Join-Path $target "Microsoft.AspNetCore.Components.WebAssembly.dll"))) {
    throw "Microsoft.AspNetCore.Components.WebAssembly.dll was not copied from $wasmLibDir"
}

$files = Get-ChildItem $target -Filter *.dll
$size = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2)
Write-Host "Prepared $($files.Count) reference assemblies ($size MB) in $target"
Write-Host "  BCL:      $netRefDir"
Write-Host "  ASP.NET:  $aspNetRefDir"
Write-Host "  Blazor:   $wasmLibDir"
