#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

BeforeAll {
    $script:Checker = Join-Path (Split-Path -Parent $PSScriptRoot) 'app/Test-HelpText.ps1'
    function New-HelpSource {
        param([string]$Body, [string]$Title = 'Test topic')
        $path = Join-Path $TestDrive 'HelpWindow.cs'
        $source = 'new HelpSection("' + $Title + '", """' + [Environment]::NewLine + $Body + [Environment]::NewLine + '""")'
        [IO.File]::WriteAllText($path, $source, (New-Object Text.UTF8Encoding($false)))
        return $path
    }
}

Describe 'Help text structure' {
    It 'checks the current Help topics' {
        $result = & $script:Checker
        $result[0] | Should -Match '^Help text checks passed: 21 topics, \d+ sentences\.$'
        $result[1] | Should -Match '^Manual checks remain:'
    }

    It 'joins wrapped lines before counting words' {
        $body = ((1..11 | ForEach-Object { 'word' }) -join ' ') + "`n" + ((1..10 | ForEach-Object { 'word' }) -join ' ') + '.'
        $path = New-HelpSource -Body $body
        { & $script:Checker -Path $path } | Should -Throw '*21 words*'
    }

    It 'accepts the sentence and paragraph limits' {
        $sentence = ((1..20 | ForEach-Object { 'word' }) -join ' ') + '.'
        $path = New-HelpSource -Body ((1..6 | ForEach-Object { $sentence }) -join ' ')
        (& $script:Checker -Path $path)[0] | Should -Match '1 topics, 6 sentences'
    }

    It 'rejects a paragraph with seven sentences' {
        $path = New-HelpSource -Body ((1..7 | ForEach-Object { 'Select Save.' }) -join ' ')
        { & $script:Checker -Path $path } | Should -Throw '*7 sentences*'
    }

    It 'counts list items separately without counting headings or markers' {
        $body = "Steps:`n" + ((1..10 | ForEach-Object { "${_}. Select Save." }) -join "`n") + "`n`nKeys:`n" + ((1..10 | ForEach-Object { '- F1 opens Help.' }) -join "`n")
        $path = New-HelpSource -Body $body
        (& $script:Checker -Path $path)[0] | Should -Match '1 topics, 20 sentences'
    }

    It 'rejects a semicolon' {
        $path = New-HelpSource -Body 'Select Save; press Enter.'
        { & $script:Checker -Path $path } | Should -Throw '*semicolon*'
    }

    It 'rejects a spaced hyphen between clauses' {
        $path = New-HelpSource -Body 'Select Save - the card changes.'
        { & $script:Checker -Path $path } | Should -Throw '*spaced hyphen*'
    }

    It 'rejects an em dash' {
        $path = New-HelpSource -Body ('Select Save' + [char]0x2014 + 'the card changes.')
        { & $script:Checker -Path $path } | Should -Throw '*em dash*'
    }

    It 'rejects an empty topic' {
        $path = New-HelpSource -Body ' '
        { & $script:Checker -Path $path } | Should -Throw '*empty topic*'
    }

    It 'rejects missing or partly unreadable sections' {
        $path = Join-Path $TestDrive 'Missing.cs'
        [IO.File]::WriteAllText($path, 'class HelpWindow {}', (New-Object Text.UTF8Encoding($false)))
        { & $script:Checker -Path $path } | Should -Throw '*Cannot read every Help topic*'
        $path = New-HelpSource -Body 'Select Save.'
        [IO.File]::AppendAllText($path, "`n" + 'new HelpSection("Other", "Plain string")', (New-Object Text.UTF8Encoding($false)))
        { & $script:Checker -Path $path } | Should -Throw '*Cannot read every Help topic*'
    }

    It 'rejects duplicate topic names' {
        $path = New-HelpSource -Body 'Select Save.'
        [IO.File]::AppendAllText($path, "`n" + [IO.File]::ReadAllText($path), (New-Object Text.UTF8Encoding($false)))
        { & $script:Checker -Path $path } | Should -Throw '*Duplicate topic*'
    }
}
