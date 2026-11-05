#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

BeforeAll {
    $publisher = Join-Path $PSScriptRoot '..\app\Publish-PromptFlipper.ps1'
    $appDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\app'))
}

Describe 'Publish output boundaries' {
    It 'rejects the source directory even with WhatIf' {
        { & $publisher -OutputPath $appDirectory -WhatIf } | Should -Throw '*Unsafe publish output*'
    }

    It 'rejects a normalized ancestor of the source directory' {
        $ancestor = Join-Path $appDirectory '..\app\..'
        { & $publisher -OutputPath $ancestor -WhatIf } | Should -Throw '*Unsafe publish output*'
    }

    It 'rejects a volume root' {
        { & $publisher -OutputPath ([IO.Path]::GetPathRoot($appDirectory)) -WhatIf } |
            Should -Throw '*Unsafe publish output*'
    }

    It 'rejects an unowned directory without changing its sentinel' {
        $output = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'unowned')
        $sentinel = Join-Path $output.FullName 'keep.txt'
        [IO.File]::WriteAllText($sentinel, 'keep')
        { & $publisher -OutputPath $output.FullName -WhatIf } | Should -Throw '*unowned*'
        [IO.File]::ReadAllText($sentinel) | Should -Be 'keep'
    }

    It 'does not create a directory under WhatIf' {
        $output = Join-Path $TestDrive 'absent'
        & $publisher -OutputPath $output -WhatIf
        Test-Path -LiteralPath $output | Should -BeFalse
    }

    It 'preserves legacy single-file output under WhatIf' {
        $output = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'legacy')
        $exe = Join-Path $output.FullName 'PromptFlipper.exe'
        [IO.File]::WriteAllText($exe, 'legacy')
        & $publisher -OutputPath $output.FullName -WhatIf
        [IO.File]::ReadAllText($exe) | Should -Be 'legacy'
    }

    It 'preserves an owned directory under WhatIf' {
        $output = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'owned')
        [IO.File]::WriteAllText((Join-Path $output.FullName '.promptflipper-publish-output'), 'owned')
        $sentinel = Join-Path $output.FullName 'stale.txt'
        [IO.File]::WriteAllText($sentinel, 'keep')
        & $publisher -OutputPath $output.FullName -WhatIf
        [IO.File]::ReadAllText($sentinel) | Should -Be 'keep'
    }

    It 'rejects a redirected destination and its descendants' {
        $target = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'target')
        $link = Join-Path $TestDrive 'redirect'
        New-Item -ItemType Junction -Path $link -Target $target.FullName | Out-Null
        try {
            { & $publisher -OutputPath $link -WhatIf } | Should -Throw '*ordinary directories*'
            { & $publisher -OutputPath (Join-Path $link 'child') -WhatIf } |
                Should -Throw '*ordinary directories*'
        }
        finally { [IO.Directory]::Delete($link) }
    }

    It 'rejects redirected contents before cleanup' {
        $output = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'nested-link')
        [IO.File]::WriteAllText((Join-Path $output.FullName '.promptflipper-publish-output'), 'owned')
        $target = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'nested-target')
        $link = Join-Path $output.FullName 'redirect'
        New-Item -ItemType Junction -Path $link -Target $target.FullName | Out-Null
        try {
            { & $publisher -OutputPath $output.FullName -WhatIf } | Should -Throw '*redirected path*'
        }
        finally { [IO.Directory]::Delete($link) }
    }
}

Describe 'Read-only executable inspection' {
    It 'reports exactly one identity without changing files or invoking dotnet' {
        $output = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'inspect')
        $exe = Join-Path $output.FullName 'PromptFlipper.exe'
        [IO.File]::WriteAllText($exe, 'synthetic executable')
        $before = Get-FileHash -LiteralPath $exe
        Mock Get-Command { throw 'Inspection must not resolve dotnet' } -ParameterFilter { $Name -eq 'dotnet' }
        $report = @(& $publisher -OutputPath $output.FullName -InspectOnly)
        $report.Count | Should -Be 1
        $report[0].Path | Should -Be $exe
        $report[0].Sha256 | Should -Be $before.Hash
        (Get-FileHash -LiteralPath $exe).Hash | Should -Be $before.Hash
        @(Get-ChildItem -LiteralPath $output.FullName -Force).Count | Should -Be 1
        Should -Invoke Get-Command -Times 0 -ParameterFilter { $Name -eq 'dotnet' }
    }

    It 'rejects an empty directory' {
        $output = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'empty')
        { & $publisher -OutputPath $output.FullName -InspectOnly } | Should -Throw '*found 0*'
    }

    It 'rejects multiple executables' {
        $output = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'multiple')
        [IO.File]::WriteAllText((Join-Path $output.FullName 'one.exe'), 'one')
        [IO.File]::WriteAllText((Join-Path $output.FullName 'two.exe'), 'two')
        { & $publisher -OutputPath $output.FullName -InspectOnly } | Should -Throw '*found 2*'
    }

    It 'does not create missing inspection output' {
        $output = Join-Path $TestDrive 'missing-inspection'
        { & $publisher -OutputPath $output -InspectOnly } | Should -Throw
        Test-Path -LiteralPath $output | Should -BeFalse
    }
}

Describe 'Publish execution' {
    BeforeEach {
        $script:FakeDotnet = Join-Path $TestDrive 'dotnet.cmd'
        [IO.File]::WriteAllText($script:FakeDotnet,
            "@echo off`r`necho build progress`r`necho synthetic>""%~6\PromptFlipper.exe""`r`nexit /b 0`r`n")
        $mockCommand = [scriptblock]::Create(
            ('[pscustomobject]@{{ Source = ''{0}'' }}' -f $script:FakeDotnet.Replace("'", "''")))
        Mock Get-Command $mockCommand -ParameterFilter { $Name -eq 'dotnet' }
    }

    It 'cleans owned output and returns one identity despite native progress output' {
        $output = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'build-output')
        [IO.File]::WriteAllText((Join-Path $output.FullName '.promptflipper-publish-output'), 'owned')
        [IO.File]::WriteAllText((Join-Path $output.FullName 'stale.txt'), 'stale')
        $report = @(& $publisher -OutputPath $output.FullName)
        $report.Count | Should -Be 1
        $report[0].Sha256 | Should -Be (Get-FileHash -LiteralPath $report[0].Path).Hash
        Test-Path -LiteralPath (Join-Path $output.FullName 'stale.txt') | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $output.FullName '.promptflipper-publish-output') | Should -BeTrue
    }

    It 'preserves unrelated content with SkipClean without claiming directory ownership' {
        $output = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'skip-clean')
        $sentinel = Join-Path $output.FullName 'keep.txt'
        [IO.File]::WriteAllText($sentinel, 'keep')
        $report = & $publisher -OutputPath $output.FullName -SkipClean
        $report.Path | Should -Be (Join-Path $output.FullName 'PromptFlipper.exe')
        [IO.File]::ReadAllText($sentinel) | Should -Be 'keep'
        Test-Path -LiteralPath (Join-Path $output.FullName '.promptflipper-publish-output') | Should -BeFalse
    }

    It 'propagates a failed publish instead of reporting stale output' {
        [IO.File]::WriteAllText($script:FakeDotnet, "@echo off`r`nexit /b 7`r`n")
        { & $publisher -OutputPath (Join-Path $TestDrive 'failed-build') } | Should -Throw '*exit code 7*'
    }
}
