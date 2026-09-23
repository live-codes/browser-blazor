# Copies the reference assemblies Roslyn needs to compile user components:
#   - Microsoft.NETCore.App.Ref    (the BCL)
#   - Microsoft.AspNetCore.App.Ref (Blazor: Microsoft.AspNetCore.Components*, ...)
# into src/BlazorRunner/refs, where they are embedded into the app assembly. Re-run after
# upgrading the .NET SDK.
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

$netRefDir = Get-LatestRefDir "Microsoft.NETCore.App.Ref"
$aspNetRefDir = Get-LatestRefDir "Microsoft.AspNetCore.App.Ref"

# Blazor must be present or user components cannot compile against it.
$blazorRefs = Get-ChildItem (Join-Path $aspNetRefDir "Microsoft.AspNetCore.Components*.dll")
if (-not $blazorRefs) { throw "Microsoft.AspNetCore.Components*.dll not found in $aspNetRefDir" }

$target = Join-Path $PSScriptRoot "..\src\BlazorRunner\refs"
New-Item -ItemType Directory -Path $target -Force | Out-Null
Get-ChildItem $target -Filter *.dll | Remove-Item -Force

# BCL first, then ASP.NET Core — skipping names already present so the BCL wins any overlap.
Copy-Item (Join-Path $netRefDir "*.dll") $target
$existing = @{}
Get-ChildItem $target -Filter *.dll | ForEach-Object { $existing[$_.Name] = $true }
Get-ChildItem (Join-Path $aspNetRefDir "*.dll") |
    Where-Object { -not $existing.ContainsKey($_.Name) } |
    Copy-Item -Destination $target

$files = Get-ChildItem $target -Filter *.dll
$size = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2)
Write-Host "Prepared $($files.Count) reference assemblies ($size MB) in $target"
Write-Host "  BCL:     $netRefDir"
Write-Host "  ASP.NET: $aspNetRefDir"
