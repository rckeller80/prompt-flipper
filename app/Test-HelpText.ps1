#Requires -Version 5.1
<#
.SYNOPSIS
Checks the text of every Help topic.

.DESCRIPTION
Reads the HelpSection and HelpGroup raw strings from HelpWindow.cs without starting the application.
A topic whose text comes from a Type.Method() call is read from that method's raw string in the sibling Type.cs.
Checks sentence length, paragraph length, semicolons and clause joins with spaced hyphens.
Each list item and each "key = value" table row counts as a separate paragraph.
Headings and list markers do not count.
The 20-word ceiling also keeps descriptions within the writing standard's 25-word limit.

These checks cover only measurable structure. Check voice, verbs, instruction meaning,
noun clusters, qualifiers and workflow accuracy manually against docs/writing-standard.md.

Execution platform: any; PowerShell: Windows PowerShell 5.1 or PowerShell 7 (portable controller).

.PARAMETER Path
Specifies a C# file that contains HelpSection raw strings. The default is the sibling HelpWindow.cs.

.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File projects/prompt-flipper/app/Test-HelpText.ps1
#>
[CmdletBinding()]
param([string]$Path)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Path)) {
    $Path = Join-Path $PSScriptRoot 'HelpWindow.cs'
}
$Path = (Resolve-Path -LiteralPath $Path).ProviderPath
$source = [IO.File]::ReadAllText($Path)
$declaration = 'new\s+Help(?:Section|Group)\("'
$rawString = "`"`"`"\r?\n(?<body>.*?)^\s*`"`"`""
$pattern = "(?ms)$declaration(?<title>[^`"\r\n]+)`",\s*(?:$rawString[,)]|(?<type>\w+)\.(?<method>\w+)\(\)\))"
$matched = [regex]::Matches($source, $pattern)
$declarations = [regex]::Matches($source, $declaration).Count
if ($matched.Count -eq 0 -or $matched.Count -ne $declarations) {
    throw "Cannot read every Help topic. Read $($matched.Count) of $declarations declarations. Expected C# raw strings or Type.Method() calls that return one."
}

# A topic body is an inline raw string, or a Type.Method() call whose expression-bodied
# method in the sibling Type.cs returns a raw string. Interpolation holes count as one word.
$sections = foreach ($match in $matched) {
    $title = $match.Groups['title'].Value
    if ($match.Groups['body'].Success) {
        $body = $match.Groups['body'].Value
    } else {
        $type = $match.Groups['type'].Value
        $method = $match.Groups['method'].Value
        $typePath = Join-Path (Split-Path -Parent $Path) "$type.cs"
        if (-not (Test-Path -LiteralPath $typePath)) {
            throw "Cannot read Help topic '$title'. $type.cs is not next to $(Split-Path -Leaf $Path)."
        }
        $typeSource = [IO.File]::ReadAllText($typePath)
        $methodBody = [regex]::Match($typeSource, "(?ms)\bstring\s+$method\(\)\s*=>\s*\`$?$rawString;")
        if (-not $methodBody.Success) {
            throw "Cannot read Help topic '$title'. $type.$method() in $type.cs does not return a raw string."
        }
        $body = $methodBody.Groups['body'].Value -replace '\{[^{}\r\n]*\}', 'value'
    }
    [pscustomobject]@{ Title = $title; Body = $body }
}

$failures = New-Object 'System.Collections.Generic.List[string]'
$titles = @{}
$sentenceCount = 0
foreach ($section in $sections) {
    $title = $section.Title
    if ($titles.ContainsKey($title)) { $failures.Add("Duplicate topic: $title") }
    $titles[$title] = $true
    $body = $section.Body.Trim()
    if ([string]::IsNullOrWhiteSpace($body)) { $failures.Add("${title}: empty topic") }
    if ($body.Contains(';')) { $failures.Add("${title}: semicolon") }
    if ($body.Contains([string][char]0x2014)) { $failures.Add("${title}: em dash") }

    foreach ($paragraph in [regex]::Split($body, '\r?\n\s*\r?\n')) {
        foreach ($item in [regex]::Split($paragraph, '(?m)(?=^\s*(?:\d+\.\s+|-\s+|[^\r\n=]+\s=\s))')) {
            $lines = @($item -split '\r?\n' | ForEach-Object {
                $line = ($_ -replace '^\s*(?:\d+\.\s+|-\s+)', '').Trim()
                if ($line -and $line -notmatch ':$') { $line }
            })
            $text = $lines -join ' '
            if ($text -match '\s-\s') { $failures.Add("${title}: spaced hyphen joins text") }
            $sentences = @([regex]::Matches($text, '[^.!?]+(?:[.!?]+|$)') | Where-Object { $_.Value.Trim() })
            if ($sentences.Count -gt 6) { $failures.Add("${title}: paragraph has $($sentences.Count) sentences (maximum 6)") }
            foreach ($sentence in $sentences) {
                $sentenceCount++
                $words = [regex]::Matches($sentence.Value.Trim(), '\S+').Count
                if ($words -gt 20) { $failures.Add("${title}: sentence has $words words (maximum 20): $($sentence.Value.Trim())") }
            }
        }
    }
}
$summary = "$(@($sections).Count) topics, $sentenceCount sentences."
if ($failures.Count -gt 0) { throw ((@("Help text checks failed: $summary") + $failures) -join [Environment]::NewLine) }
Write-Output "Help text checks passed: $summary"
Write-Output 'Manual checks remain: voice, verbs, instruction meaning, noun clusters, qualifiers and workflow accuracy.'
