#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes InventoryTracker as a single self-contained executable into dist/.

.DESCRIPTION
    Produces one portable .exe with no .NET runtime prerequisite. Native
    dependencies (SQLite) are bundled into the executable rather than being
    extracted beside it at run time.

.PARAMETER FrameworkDependent
    Build against an installed .NET runtime instead of bundling one. Produces a
    far smaller executable, but the target machine needs the ASP.NET Core and
    Windows Desktop runtimes.

.PARAMETER Runtime
    Target runtime identifier. Defaults to win-x64.

.PARAMETER Version
    Version to stamp into the assembly. Defaults to the contents of the VERSION
    file. CI always passes its own computed version explicitly instead (see
    ci.yml) — it may be ahead of VERSION when a merge didn't bump the file — so
    a local build without -Version can lag one patch behind the latest release.

.PARAMETER OutputPath
    Publishes into a dedicated output directory without inspecting or stopping
    running processes and without changing dist/. The directory must not exist
    or be empty.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -FrameworkDependent
    .\publish.ps1 -OutputPath $env:TEMP\InventoryTracker-smoke
#>
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Version,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'src\InventoryTracker.csproj'
$dist = Join-Path $root 'dist'
$isolatedOutput = -not [string]::IsNullOrWhiteSpace($OutputPath)
$output = $dist

function Assert-NoReparsePointInPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $current = [IO.Path]::GetPathRoot($fullPath)
    $relativePath = $fullPath.Substring($current.Length)
    foreach ($component in $relativePath.Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $component
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Label contains a reparse point, so its physical location cannot be verified safely: $current"
            }
        }
    }
}

# Check every existing component before comparing lexical paths. A junction or
# symbolic link can make an apparently isolated output resolve into dist/.
Assert-NoReparsePointInPath -Path $root -Label 'Repository path'
Assert-NoReparsePointInPath -Path $dist -Label 'dist path'
if ($isolatedOutput) {
    $output = [IO.Path]::GetFullPath($OutputPath)
    Assert-NoReparsePointInPath -Path $output -Label 'OutputPath'
    $distFullPath = [IO.Path]::GetFullPath($dist).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $outputPrefix = $distFullPath + [IO.Path]::DirectorySeparatorChar
    if ($output.Equals($distFullPath, [StringComparison]::OrdinalIgnoreCase) -or
        $output.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "OutputPath must be outside dist/: $output"
    }
    if (Test-Path -LiteralPath $output) {
        if (-not (Test-Path -LiteralPath $output -PathType Container)) {
            throw "OutputPath must be a directory: $output"
        }
        if (@(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) {
            throw "OutputPath must be new or empty: $output"
        }
    }
} else {
    $dist = [IO.Path]::GetFullPath($dist)
}
$versionFile = Join-Path $root 'VERSION'

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

if (-not $Version) {
    if (-not (Test-Path $versionFile)) {
        throw "No -Version given and no VERSION file at $versionFile"
    }
    $Version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
}

# A running instance locks its own exe, which would otherwise fail the clean below
# with an opaque "access denied" instead of saying what's actually holding it. Matched
# by directory rather than a fixed name/path so a stale exe left over from before a
# rename (e.g. an old LogParser.exe still running out of dist/) is still caught.
if (-not $isolatedOutput) {
    $distPrefix = $dist.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            if (-not $_.Path) { return $false }
            $processPath = [IO.Path]::GetFullPath($_.Path)
            $processPath.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object {
            Write-Host "Stopping running instance (pid $($_.Id))" -ForegroundColor Yellow
            Stop-Process -Id $_.Id -Force
            $_.WaitForExit(5000) | Out-Null
        }

    # Start clean only in the known local dist directory.
    if (Test-Path -LiteralPath $dist) {
        Remove-Item -LiteralPath $dist -Recurse -Force
    }
}
if (-not (Test-Path -LiteralPath $output)) {
    New-Item -ItemType Directory -Path $output -Force | Out-Null
}

$selfContained = -not $FrameworkDependent

Write-Host "Publishing InventoryTracker" -ForegroundColor Cyan
Write-Host "  version       : $Version"
Write-Host "  runtime       : $Runtime"
Write-Host "  configuration : $Configuration"
Write-Host "  self-contained: $selfContained"
Write-Host "  output        : $output"
Write-Host ''

$arguments = @(
    'publish', $project
    '--configuration', $Configuration
    '--runtime', $Runtime
    "--self-contained:$($selfContained.ToString().ToLowerInvariant())"
    '--output', $output
    '--nologo'
    '-p:PublishSingleFile=true'
    # Without this the bundled SQLite native library is unpacked next to the exe
    # on first run, which would defeat the point of a single file.
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:EnableCompressionInSingleFile=true'
    '-p:DebugType=none'
    '-p:GenerateDocumentationFile=false'
    '-p:SatelliteResourceLanguages=en'
    "-p:Version=$Version"
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Publishing drops a .pdb and a runtime config beside the exe; neither is needed
# to run, and leaving them makes "single file" a lie.
Get-ChildItem -LiteralPath $output -File |
    Where-Object { $_.Extension -ne '.exe' } |
    Remove-Item -Force

Get-ChildItem -LiteralPath $output -Directory |
    Where-Object { (Get-ChildItem -LiteralPath $_.FullName -Recurse -File).Count -eq 0 } |
    Remove-Item -Recurse -Force

$exe = Join-Path $output 'InventoryTracker.exe'
if (-not (Test-Path $exe)) {
    throw "Expected executable was not produced: $exe"
}

$leftovers = Get-ChildItem -LiteralPath $output -Recurse | Where-Object { $_.FullName -ne $exe }
if ($leftovers) {
    Write-Warning "Output directory contains more than the executable:"
    $leftovers | ForEach-Object { Write-Warning "  $($_.FullName.Substring($output.Length + 1))" }
}

$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ''
Write-Host "Done: $exe ($sizeMb MB)" -ForegroundColor Green
Write-Host ''
Write-Host 'Run it:'
Write-Host '  InventoryTracker.exe                     tray app + UI at http://localhost:5730'
Write-Host '  InventoryTracker.exe --scan --holdings   console mode, no UI'
