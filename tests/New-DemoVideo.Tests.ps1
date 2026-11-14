#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Tests the cue, timeline and subtitle logic of New-DemoVideo.ps1 without running ffmpeg.
.DESCRIPTION
    Execution platform: Windows; PowerShell: Windows PowerShell 5.1.
#>

BeforeAll {
    $script:DemoFolder = Join-Path $PSScriptRoot '..\artifacts\demo'
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $script:DemoFolder 'New-DemoVideo.ps1'), [ref]$tokens, [ref]$errors)
    $script:ParseErrors = $errors
    foreach ($fn in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
        . ([scriptblock]::Create($fn.Extent.Text))
    }
    $script:Ranges = @(
        [pscustomobject]@{ cue = 1; start = 0.0; end = 2.0 },
        [pscustomobject]@{ cue = 2; start = 2.0; end = 3.0 },
        [pscustomobject]@{ cue = 2; start = 4.0; end = 5.5 },
        [pscustomobject]@{ cue = 3; start = 6.0; end = 7.0 }
    )
}

Describe 'New-DemoVideo' {
    It 'parses without errors' { $script:ParseErrors.Count | Should -Be 0 }

    It 'reads seven subtitles from the demo script' {
        $texts = Get-DemoCueText ([IO.File]::ReadAllLines((Join-Path $script:DemoFolder 'demo-script.md')))
        $texts.Count | Should -Be 7
        $texts[0] | Should -Be 'Asking an AI for help? Your message holds private details.'
    }

    It 'refuses a script without a Scenes table' {
        { Get-DemoCueText @('# x', '## Other', '| 1. a | b |') } | Should -Throw '*no numbered rows*'
    }

    It 'maps ranges to raw and cut time' {
        $t = @(Get-CutTimeline $script:Ranges 0.8)
        $t[2].rawStart | Should -Be 4.8
        $t[2].cutStart | Should -Be 3.0
        $t[3].cutEnd | Should -Be 5.5
    }

    It 'joins the ranges of one cue into one span' {
        $s = @(Get-CueSpan @(Get-CutTimeline $script:Ranges 0))
        $s.Count | Should -Be 3
        $s[1].start | Should -Be 2.0
        $s[1].end | Should -Be 4.5
    }

    It 'formats SRT times' { Format-SrtTime 65.4321 | Should -Be '00:01:05,432' }

    It 'refuses a cue count that does not match the subtitles' {
        $s = @(Get-CueSpan @(Get-CutTimeline $script:Ranges 0))
        { New-SrtText $s @('a', 'b') } | Should -Throw '*3 cues*2 subtitles*'
    }

    It 'writes one SRT block per cue' {
        $srt = New-SrtText @(Get-CueSpan @(Get-CutTimeline $script:Ranges 0)) @('a', 'b', 'c')
        $srt | Should -Match "2`n00:00:02,000 --> 00:00:04,500`nb"
    }

    It 'builds a select filter with one term per range' {
        $f = Get-SelectFilter @(Get-CutTimeline $script:Ranges 0.5)
        $f | Should -Be "select='gte(t,0.5)*lt(t,2.5)+gte(t,2.5)*lt(t,3.5)+gte(t,4.5)*lt(t,6)+gte(t,6.5)*lt(t,7.5)',setpts=N/30/TB"
    }
}
