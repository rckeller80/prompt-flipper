#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

BeforeAll {
    $packager = Join-Path $PSScriptRoot '..\app\Build-PromptFlipperPackage.ps1'
}

Describe 'Package publishing orchestration' {
    It 'does not create output or inspect input under WhatIf' {
        $output = Join-Path $TestDrive 'package'
        & $packager -SkipPublish -PublishPath (Join-Path $TestDrive 'missing') -OutputPath $output -WhatIf
        Test-Path -LiteralPath $output | Should -BeFalse
    }

    It 'rejects missing SkipPublish input before resolving signing tools' {
        { & $packager -SkipPublish -PublishPath (Join-Path $TestDrive 'missing') } |
            Should -Throw '*publish folder does not exist*'
    }

    It 'uses publisher validation for empty SkipPublish output' {
        $inputDirectory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'empty')
        { & $packager -SkipPublish -PublishPath $inputDirectory.FullName } | Should -Throw '*found 0*'
    }

    It 'uses publisher validation for ambiguous SkipPublish output' {
        $inputDirectory = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'ambiguous')
        [IO.File]::WriteAllText((Join-Path $inputDirectory.FullName 'one.exe'), 'one')
        [IO.File]::WriteAllText((Join-Path $inputDirectory.FullName 'two.exe'), 'two')
        { & $packager -SkipPublish -PublishPath $inputDirectory.FullName } | Should -Throw '*found 2*'
    }
}
