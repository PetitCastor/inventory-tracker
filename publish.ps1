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

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'src\InventoryTracker.csproj'
$dist = Join-Path $root 'dist'

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

# A running instance locks its own exe, which would otherwise fail the clean below
# with an opaque "access denied" instead of saying what's actually holding it. Matched
# by directory rather than a fixed name/path so a stale exe left over from before a
# rename (e.g. an old LogParser.exe still running out of dist/) is still caught.
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        Write-Host "Stopping running instance (pid $($_.Id))" -ForegroundColor Yellow
        Stop-Process -Id $_.Id -Force
        $_.WaitForExit(5000) | Out-Null
    }

# Start clean so a stale exe can never be mistaken for a fresh build.
if (Test-Path $dist) {
    Remove-Item -LiteralPath $dist -Recurse -Force
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null

$selfContained = -not $FrameworkDependent

Write-Host "Publishing InventoryTracker" -ForegroundColor Cyan
Write-Host "  runtime       : $Runtime"
Write-Host "  configuration : $Configuration"
Write-Host "  self-contained: $selfContained"
Write-Host "  output        : $dist"
Write-Host ''

$arguments = @(
    'publish', $project
    '--configuration', $Configuration
    '--runtime', $Runtime
    "--self-contained:$($selfContained.ToString().ToLowerInvariant())"
    '--output', $dist
    '--nologo'
    '-p:PublishSingleFile=true'
    # Without this the bundled SQLite native library is unpacked next to the exe
    # on first run, which would defeat the point of a single file.
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:EnableCompressionInSingleFile=true'
    '-p:DebugType=none'
    '-p:GenerateDocumentationFile=false'
    '-p:SatelliteResourceLanguages=en'
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Publishing drops a .pdb and a runtime config beside the exe; neither is needed
# to run, and leaving them makes "single file" a lie.
Get-ChildItem -LiteralPath $dist -File |
    Where-Object { $_.Extension -ne '.exe' } |
    Remove-Item -Force

Get-ChildItem -LiteralPath $dist -Directory |
    Where-Object { (Get-ChildItem -LiteralPath $_.FullName -Recurse -File).Count -eq 0 } |
    Remove-Item -Recurse -Force

$exe = Join-Path $dist 'InventoryTracker.exe'
if (-not (Test-Path $exe)) {
    throw "Expected executable was not produced: $exe"
}

$leftovers = Get-ChildItem -LiteralPath $dist -Recurse | Where-Object { $_.FullName -ne $exe }
if ($leftovers) {
    Write-Warning "dist/ contains more than the executable:"
    $leftovers | ForEach-Object { Write-Warning "  $($_.FullName.Substring($dist.Length + 1))" }
}

$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ''
Write-Host "Done: $exe ($sizeMb MB)" -ForegroundColor Green
Write-Host ''
Write-Host 'Run it:'
Write-Host '  InventoryTracker.exe                     tray app + UI at http://localhost:5730'
Write-Host '  InventoryTracker.exe --scan --holdings   console mode, no UI'
