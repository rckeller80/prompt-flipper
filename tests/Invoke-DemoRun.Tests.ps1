#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Tests the keep-range bookkeeping of Invoke-DemoRun.ps1 without driving the app.
.DESCRIPTION
    Execution platform: Windows; PowerShell: Windows PowerShell 5.1.
#>

BeforeAll {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\artifacts\demo\Invoke-DemoRun.ps1'), [ref]$tokens, [ref]$errors)
    $script:ParseErrors = $errors
    foreach ($name in 'Add-KeepRange', 'Save-KeepRangeFile') {
        $fn = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
        . ([scriptblock]::Create($fn.Extent.Text))
    }
}

Describe 'Invoke-DemoRun keep-ranges' {
    BeforeEach { $script:list = New-Object System.Collections.Generic.List[object] }

    It 'parses without errors' { $script:ParseErrors.Count | Should -Be 0 }

    It 'adds ranges in order' {
        Add-KeepRange $script:list 1 0 1.23456
        Add-KeepRange $script:list 2 1.5 2
        $script:list.Count | Should -Be 2
        $script:list[0].end | Should -Be 1.235
    }

    It 'accepts a range that starts where the rounded previous range ends' {
        Add-KeepRange $script:list 2 10 12.4239303
        { Add-KeepRange $script:list 3 12.4239303 13 } | Should -Not -Throw
    }

    It 'refuses an empty range' { { Add-KeepRange $script:list 1 2 2 } | Should -Throw '*not after*' }

    It 'refuses an overlapping range' {
        Add-KeepRange $script:list 1 0 2
        { Add-KeepRange $script:list 2 1 3 } | Should -Throw '*before the previous*'
    }

    It 'refuses a cue that goes back' {
        Add-KeepRange $script:list 2 0 1
        { Add-KeepRange $script:list 1 1 2 } | Should -Throw '*must not go back*'
    }

    It 'writes the file that New-DemoVideo reads' {
        Add-KeepRange $script:list 1 0 1
        $path = Join-Path $TestDrive 'raw.mp4.keep.json'
        Save-KeepRangeFile $path 0.8 $script:list
        $doc = Get-Content -Raw $path | ConvertFrom-Json
        $doc.leadSeconds | Should -Be 0.8
        @($doc.ranges).Count | Should -Be 1
        [IO.File]::ReadAllBytes($path)[0] | Should -Be ([byte][char]'{')
    }
}
