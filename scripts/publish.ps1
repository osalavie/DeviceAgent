#Requires -Version 5.1
# Publishes Sim.DeviceAgent.App. Default: self-contained single-file win-x64 with example configs,
# trimmed (PublishTrimmed) and compressed (EnableCompressionInSingleFile) for a small package.
# Usage: powershell -File scripts/publish.ps1 [output-dir] [parameters]
[CmdletBinding()]
param(
    [Alias("o")]
    [Parameter(Position = 0)]
    [string]$Output,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64",

    [switch]$NoSingleFile,

    [switch]$FrameworkDependent,

    [switch]$NoTrim,

    [switch]$NoCompress,

    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$ToolRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$defaultOutput = Join-Path $ToolRoot (Join-Path "artifacts\publish" $RuntimeIdentifier)
$useDefaultOutput = [string]::IsNullOrWhiteSpace($Output)
if ($useDefaultOutput) {
    $Output = $defaultOutput
}
$Output = [System.IO.Path]::GetFullPath($Output)

$toolRootPrefix = $ToolRoot.TrimEnd('\') + '\'
$outputPrefix = $Output.TrimEnd('\') + '\'
if ($toolRootPrefix.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish into output directory: $Output"
}

$defaultOutputFull = [System.IO.Path]::GetFullPath($defaultOutput)
if ($Output.TrimEnd('\') -eq $defaultOutputFull.TrimEnd('\')) {
    $useDefaultOutput = $true
}
$shouldClean = $useDefaultOutput -or $Clean

if (Test-Path -LiteralPath $Output) {
    if ($shouldClean) {
        Write-Host "Cleaning $Output"
        Remove-Item -LiteralPath $Output -Recurse -Force
    }
    else {
        Write-Host "Keeping existing files in $Output (use -Clean to clear)"
    }
}
New-Item -ItemType Directory -Path $Output -Force | Out-Null

$selfContainedValue = if ($FrameworkDependent) { "false" } else { "true" }
$singleFile = -not $NoSingleFile
$trim = -not $FrameworkDependent -and -not $NoTrim
$compress = $singleFile -and -not $FrameworkDependent -and -not $NoCompress
$dotnetArgs = @(
    "publish",
    (Join-Path $ToolRoot "src\Sim.DeviceAgent.App"),
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "-o", $Output,
    "--self-contained", $selfContainedValue
)
if ($singleFile) {
    $dotnetArgs += "-p:PublishSingleFile=true"
}
if ($trim) {
    $dotnetArgs += "-p:PublishTrimmed=true"
}
if ($compress) {
    $dotnetArgs += "-p:EnableCompressionInSingleFile=true"
}

Write-Host "Publishing Sim.DeviceAgent.App ($Configuration/$RuntimeIdentifier, self-contained=$selfContainedValue, single-file=$singleFile, trim=$trim, compress=$compress)"
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $ToolRoot "src\Sim.DeviceAgent.App\agent.example.json") -Destination $Output -Force

Write-Host "Published to $Output"
Write-Host "Copy agent.example.json to agent.json next to the exe and edit it for the site."
