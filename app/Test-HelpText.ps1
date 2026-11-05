#Requires -Version 5.1
<#
.SYNOPSIS
Checks the text of every Help topic.

.DESCRIPTION
Reads the HelpSection raw strings from HelpWindow.cs without starting the application.
Checks sentence length, paragraph length, semicolons and clause joins with spaced hyphens.
Each list item counts as a separate paragraph. Headings and list markers do not count.
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
$source = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Path).ProviderPath)
$declaration = 'new\s+Help(?:Section|Group)\("'
$sections = [regex]::Matches($source, "(?ms)$declaration(?<title>[^`"\r\n]+)`",\s*`"`"`"\r?\n(?<body>.*?)^\s*`"`"`"[,)]")
$declarations = [regex]::Matches($source, $declaration).Count
if ($sections.Count -eq 0 -or $sections.Count -ne $declarations) {
    throw 'Cannot read every Help topic. Expected named sections with C# raw strings.'
}

$failures = New-Object 'System.Collections.Generic.List[string]'
$titles = @{}
$sentenceCount = 0
foreach ($section in $sections) {
    $title = $section.Groups['title'].Value
    if ($titles.ContainsKey($title)) { $failures.Add("Duplicate topic: $title") }
    $titles[$title] = $true
    $body = $section.Groups['body'].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($body)) { $failures.Add("${title}: empty topic") }
    if ($body.Contains(';')) { $failures.Add("${title}: semicolon") }
    if ($body.Contains([string][char]0x2014)) { $failures.Add("${title}: em dash") }

    foreach ($paragraph in [regex]::Split($body, '\r?\n\s*\r?\n')) {
        foreach ($item in [regex]::Split($paragraph, '(?m)(?=^\s*(?:\d+\.\s+|-\s+))')) {
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
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
Write-Output "Help text checks passed: $($sections.Count) topics, $sentenceCount sentences."
Write-Output 'Manual checks remain: voice, verbs, instruction meaning, noun clusters, qualifiers and workflow accuracy.'
