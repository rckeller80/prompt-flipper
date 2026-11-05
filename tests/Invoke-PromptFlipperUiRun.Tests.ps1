#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Tests live popup discovery and control activation against the built app.
.DESCRIPTION
    Build the Debug app before this suite runs. The app must have a saved, accessible card folder.
    Execution platform: Windows; PowerShell: Windows PowerShell 5.1.
#>

param(
    [string]$AppPath = (Join-Path $PSScriptRoot '..\app\bin\Debug\net8.0-windows\win-x64\PromptFlipper.exe')
)

BeforeAll {
    $script:AppPath = (Resolve-Path -LiteralPath $AppPath -ErrorAction Stop).Path
    $script:UiRun = Join-Path $PSScriptRoot '..\app\Invoke-PromptFlipperUiRun.ps1'
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($script:UiRun, [ref]$tokens, [ref]$errors)
    $helper = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-UiHost'
    }, $true)
    . ([scriptblock]::Create($helper.Extent.Text))
}

Describe 'PowerShell host resolution' {
    It 'returns the concrete executable path including spaces' {
        $path = Resolve-UiHost -Resolver {
            [pscustomobject]@{ Path = 'C:\Program Files\PowerShell\7\pwsh.exe'; PermissionMode = 'Direct' }
        }
        $path | Should -Be 'C:\Program Files\PowerShell\7\pwsh.exe'
    }

    It 'reports a denied launch without an alias fallback' {
        { Resolve-UiHost -Resolver {
            [pscustomobject]@{ Path = 'C:\installed\pwsh.exe'; PermissionMode = 'Escalated' }
        } } | Should -Throw '*requires permission*C:\installed\pwsh.exe*'
    }

    It 'reports an absent installation' {
        { Resolve-UiHost -Resolver { $null } } | Should -Throw '*not found by Resolve-PowerShell7*'
    }

    It 'rejects an unusable resolver result' {
        { Resolve-UiHost -Resolver {
            [pscustomobject]@{ Path = ''; PermissionMode = 'Direct' }
        } } | Should -Throw '*no usable executable*'
    }
}

Describe 'Prompt Flipper live UI automation' {
    It 'reports and captures every font size once' {
        $outputDirectory = Join-Path $TestDrive 'font-menu'
        $lines = @(& $script:UiRun -Path $script:AppPath -WindowName 'Prompt Flipper' `
            -OpenControl 'Text Size' -Key '{ESC}' -OutputDirectory $outputDirectory)
        $report = $lines -join "`n"
        foreach ($size in 8..24) {
            @($lines | Where-Object { $_ -match "name='$size pt'" }).Count | Should -Be 1
        }
        $report | Should -Match "window opened: 'Text Size'"
        $report | Should -Match 'key \{ESC\} -> '
        $images = @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*.png')
        $images.Count | Should -Be 1
        $images[0].Length | Should -BeGreaterThan 0
        @($lines | Where-Object { $_ -like 'window opened:*' }).Count | Should -Be 1
    }

    It 'keeps the Enter fallback for a toolbar button that opens Settings' {
        $outputDirectory = Join-Path $TestDrive 'settings'
        $report = (& $script:UiRun -Path $script:AppPath -WindowName 'Settings' `
            -OpenControl 'Settings' -OutputDirectory $outputDirectory) -join "`n"
        $report | Should -Match "window opened: 'Settings'"
        $report | Should -Match "name='Review'"
        @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*Settings.png').Count | Should -Be 1
    }

    It 'reports the main window when no opener is specified' {
        $outputDirectory = Join-Path $TestDrive 'main'
        $report = (& $script:UiRun -Path $script:AppPath -WindowName 'Prompt Flipper' `
            -OutputDirectory $outputDirectory) -join "`n"
        $report | Should -Match "window opened: 'Prompt Flipper"
        $report | Should -Match "name='Text Size'"
        @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*.png').Count | Should -Be 1
    }

    It 'hosts a dialog through the resolved shell' {
        $outputDirectory = Join-Path $TestDrive 'hosted'
        $assemblyPath = [IO.Path]::ChangeExtension($script:AppPath, '.dll')
        $report = (& $script:UiRun -Path $assemblyPath -HostType PromptNavigator.SanitizerDialog `
            -HostArgument 'synthetic card text', 'Host fixture' -WindowName 'Settings' `
            -OutputDirectory $outputDirectory) -join "`n"
        $report | Should -Match "window opened: 'Settings'"
        @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*Settings.png').Count | Should -Be 1
    }
}
