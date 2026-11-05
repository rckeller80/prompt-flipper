<#
.SYNOPSIS
Publishes Prompt Flipper as one self-contained executable and reports the file identity a
tester needs to confirm the copy on the target machine.

.DESCRIPTION
The release build is a self-contained, single-file win-x64 executable: the target machine
needs no .NET runtime installed. This script is the only supported way to produce it, so the
publish arguments, the output location and the integrity report do not drift between releases.

It runs the publish, asserts that exactly one executable was produced, and reports that file's
full path, size and SHA-256 hash. The hash is what the tester recomputes on the target machine
with Get-FileHash to prove the copy that arrived is the copy that was built.

The runtime is pinned into the executable. A .NET servicing release does not reach it through
Windows Update; it reaches it by running this script again on an updated SDK. See
../docs/release-packaging.md.

.PARAMETER OutputPath
Directory to publish into. Defaults to a "publish" folder beside this script, which is
ignored by git. The folder is emptied before each publish unless -SkipClean is given.

.PARAMETER Configuration
MSBuild configuration. Defaults to Release. Debug is accepted for local diagnosis and is
never the configuration a build handed to a tester is produced with.

.PARAMETER SkipClean
Retains existing output files while publishing. Use InspectOnly to inspect an existing executable.

.PARAMETER InspectOnly
Reports an existing executable without building or changing the output directory.

.EXAMPLE
.\Publish-PromptFlipper.ps1

Publishes to .\publish and prints the path, size and SHA-256 of PromptFlipper.exe.

.EXAMPLE
.\Publish-PromptFlipper.ps1 -OutputPath 'D:\handover\prompt-flipper'

Publishes into a folder that is ready to copy to the target machine.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath,

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$SkipClean,

    [switch]$InspectOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path -Path $scriptRoot -ChildPath 'PromptFlipper.csproj'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "The project file was not found: $projectPath"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path -Path $scriptRoot -ChildPath 'publish'
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)

if (-not $InspectOnly) {
    # Validate before ShouldProcess so WhatIf also reports unsafe destinations.
    $trimChars = [char[]]@('\', '/')
    $outputDirectory = $fullOutputPath.TrimEnd($trimChars)
    $sourceDirectory = $scriptRoot.TrimEnd($trimChars)
    if ($outputDirectory -eq [System.IO.Path]::GetPathRoot($fullOutputPath).TrimEnd($trimChars) -or
        $sourceDirectory.Equals($outputDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceDirectory.StartsWith($outputDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe publish output directory: $fullOutputPath"
    }

    $ancestor = $fullOutputPath
    while (-not [string]::IsNullOrEmpty($ancestor)) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (-not $item.PSIsContainer -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Publish output must use ordinary directories: $ancestor"
            }
        }
        $ancestor = [System.IO.Path]::GetDirectoryName($ancestor)
    }

    $markerPath = Join-Path $fullOutputPath '.promptflipper-publish-output'
    if ((Test-Path -LiteralPath $fullOutputPath) -and -not $SkipClean) {
        $children = @(Get-ChildItem -LiteralPath $fullOutputPath -Force)
        $redirected = @(Get-ChildItem -LiteralPath $fullOutputPath -Force -Recurse |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if ($redirected.Count -gt 0) {
            throw "Publish output contains a redirected path: $($redirected[0].FullName)"
        }
        # Accept legacy single-file output without deleting unrelated existing content.
        $unexpected = @($children | Where-Object { $_.Name -ne 'PromptFlipper.exe' -or $_.PSIsContainer })
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -and $unexpected.Count -gt 0) {
            throw "Refusing to clean an unowned publish directory: $fullOutputPath"
        }
    }

    if (-not $PSCmdlet.ShouldProcess($fullOutputPath, "Publish $Configuration build of Prompt Flipper")) {
        return
    }

    $dotnet = Get-Command -Name 'dotnet' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $dotnet) {
        throw 'dotnet was not found on PATH. Install the .NET 8 SDK and open a new shell.'
    }

    if ((Test-Path -LiteralPath $fullOutputPath) -and -not $SkipClean) {
        Remove-Item -LiteralPath $fullOutputPath -Recurse -Force
    }
    [System.IO.Directory]::CreateDirectory($fullOutputPath) | Out-Null
    if (-not $SkipClean) {
        [IO.File]::WriteAllText($markerPath, 'Prompt Flipper publish output', [Text.UTF8Encoding]::new($false))
    }

    # The project owns runtime and single-file settings.
    & $dotnet.Source publish $projectPath --configuration $Configuration --output $fullOutputPath --nologo |
        Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
elseif (-not $PSCmdlet.ShouldProcess($fullOutputPath, 'Inspect existing Prompt Flipper executable')) {
    return
}

$executables = @(Get-ChildItem -LiteralPath $fullOutputPath -Filter '*.exe' -File)
if ($executables.Count -ne 1) {
    $names = ($executables | Select-Object -ExpandProperty Name) -join ', '
    throw "Expected exactly one executable in '$fullOutputPath' but found $($executables.Count): $names"
}

$executable = $executables[0]
$hash = Get-FileHash -LiteralPath $executable.FullName -Algorithm SHA256

[pscustomobject]@{
    Path          = $executable.FullName
    SizeMB        = [math]::Round($executable.Length / 1MB, 1)
    Sha256        = $hash.Hash
    Configuration = $Configuration
    SelfContained = $true
    BuiltUtc      = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')
}
