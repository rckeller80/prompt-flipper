[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$TargetFolder,

    [ValidateNotNullOrEmpty()]
    [string]$Separator = '-----------------------------------',

    [string]$PromptStartPattern,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-PromptCardTitle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Body,

        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 999999)]
        [int]$Index
    )

    $firstContentLine = $Body -split "`n" |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -gt 0 } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($firstContentLine)) {
        return ('Prompt {0:D3}' -f $Index)
    }

    $title = $firstContentLine -replace '^#+\s*', ''
    $title = $title -replace '\s+', ' '

    foreach ($invalidCharacter in [System.IO.Path]::GetInvalidFileNameChars()) {
        $title = $title.Replace([string]$invalidCharacter, '-')
    }

    $title = $title.Trim().TrimEnd('.', ' ')
    if ($title.Length -gt 100) {
        $title = $title.Substring(0, 100).TrimEnd('.', ' ')
    }

    if ([string]::IsNullOrWhiteSpace($title)) {
        return ('Prompt {0:D3}' -f $Index)
    }

    # GetInvalidFileNameChars does not cover the Windows reserved device names, which stay
    # reserved with an extension. 'CON.md' addresses a device, not a file in the folder.
    $reservedNames = @('CON', 'PRN', 'AUX', 'NUL')
    foreach ($ordinal in 1..9) {
        $reservedNames += ('COM{0}' -f $ordinal)
        $reservedNames += ('LPT{0}' -f $ordinal)
    }

    if ($reservedNames -contains $title.ToUpperInvariant()) {
        $title = '{0}-prompt' -f $title
    }

    return $title
}

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "The source file does not exist: $SourcePath"
}

if ($Separator.IndexOfAny([char[]]@("`r", "`n")) -ge 0) {
    throw 'Separator must be one line of literal text.'
}

$fullSourcePath = [System.IO.Path]::GetFullPath($SourcePath)
$fullTargetFolder = [System.IO.Path]::GetFullPath($TargetFolder)
$sourceText = [System.IO.File]::ReadAllText($fullSourcePath)
$sourceText = $sourceText -replace "`r`n", "`n"
$sourceLines = [regex]::Split($sourceText, "`n")
$promptBodies = New-Object System.Collections.Generic.List[string]
$currentBody = New-Object System.Collections.Generic.List[string]
$promptStartRegex = $null

if (-not [string]::IsNullOrWhiteSpace($PromptStartPattern)) {
    try {
        # The pattern is matched once per source line. A match timeout bounds a pattern that
        # backtracks catastrophically, so a bad pattern fails instead of hanging the import.
        $promptStartRegex = New-Object System.Text.RegularExpressions.Regex(
            $PromptStartPattern,
            [System.Text.RegularExpressions.RegexOptions]::None,
            [timespan]::FromSeconds(2))
    }
    catch {
        throw "PromptStartPattern is not a valid regular expression: $($_.Exception.Message)"
    }
}

foreach ($line in $sourceLines) {
    if ($line.Trim() -ceq $Separator) {
        $promptBodies.Add(($currentBody -join "`n"))
        $currentBody.Clear()
        continue
    }

    $startsPrompt = $null -ne $promptStartRegex -and $promptStartRegex.IsMatch($line)
    $currentText = $currentBody -join ''
    if ($startsPrompt -and -not [string]::IsNullOrWhiteSpace($currentText)) {
        $promptBodies.Add(($currentBody -join "`n"))
        $currentBody.Clear()
    }

    $currentBody.Add($line)
}

$promptBodies.Add(($currentBody -join "`n"))
$usedFileNames = @{}
$cards = New-Object System.Collections.Generic.List[object]

for ($promptIndex = 0; $promptIndex -lt $promptBodies.Count; $promptIndex++) {
    $body = $promptBodies[$promptIndex].Trim([char[]]@("`r", "`n"))
    $title = ConvertTo-PromptCardTitle -Body $body -Index ($promptIndex + 1)
    $candidateTitle = $title
    $suffix = 2

    while ($usedFileNames.ContainsKey($candidateTitle)) {
        $candidateTitle = '{0} ({1})' -f $title, $suffix
        $suffix++
    }

    $usedFileNames[$candidateTitle] = $true
    $targetPath = Join-Path -Path $fullTargetFolder -ChildPath ($candidateTitle + '.md')
    $content = $body -replace "`n", "`r`n"
    if (-not $content.EndsWith("`r`n")) {
        $content += "`r`n"
    }

    $cards.Add([pscustomobject]@{
            Index      = $promptIndex + 1
            Title      = $candidateTitle
            TargetPath = $targetPath
            Content    = $content
        })
}

if (-not $Force) {
    $conflicts = @($cards | Where-Object { Test-Path -LiteralPath $_.TargetPath })
    if ($conflicts.Count -gt 0) {
        $conflictList = ($conflicts.TargetPath -join [Environment]::NewLine)
        throw "Import would overwrite existing paths. Use -Force to replace them:$([Environment]::NewLine)$conflictList"
    }
}

$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
if ($PSCmdlet.ShouldProcess($fullTargetFolder, "Import $($cards.Count) prompt cards")) {
    [System.IO.Directory]::CreateDirectory($fullTargetFolder) | Out-Null

    foreach ($card in $cards) {
        [System.IO.File]::WriteAllText($card.TargetPath, $card.Content, $utf8WithoutBom)
        [pscustomobject]@{
            Index      = $card.Index
            Title      = $card.Title
            TargetPath = $card.TargetPath
        }
    }
}
