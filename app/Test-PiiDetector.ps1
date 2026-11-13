#Requires -Version 7.4
<#
.SYNOPSIS
Checks PII detection against fictional fixtures.

.DESCRIPTION
Loads the built PromptFlipper.dll and runs PiiDetector.Detect on one fixture per case.
A case for a covered category passes when the detector reports that category for the expected value.
A case for a category that is not covered, such as a name or a street address, is reported and does not fail.
The check also fails when the detector reports an IBAN as a card number or as a phone number.
All fixture values are fictional. The UK phone numbers use the Ofcom drama range.

Execution platform: Windows; PowerShell: PowerShell 7.4 or later, which runs on .NET 8.

.PARAMETER AssemblyPath
Specifies the built PromptFlipper.dll. The default is the Debug build output of the sibling project.

.PARAMETER TimingKilobytes
Specifies the size of the card that Review time is measured on. 0 skips the measurement.

.EXAMPLE
pwsh -NoProfile -File projects/prompt-flipper/app/Test-PiiDetector.ps1
#>
[CmdletBinding()]
param([string]$AssemblyPath, [int]$TimingKilobytes = 50)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    $AssemblyPath = Join-Path $PSScriptRoot 'bin\Debug\net8.0-windows\win-x64\PromptFlipper.dll'
}
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).ProviderPath)
$detector = $assembly.GetType('PromptFlipper.PiiDetector', $true)
$covered = @($detector.GetProperty('CoveredCategories').GetValue($null))

# Category, fixture text, expected detected value.
$cases = @(
    ,@('Email address', 'Write to sarah.collins@mail.example today.', 'sarah.collins@mail.example')
    ,@('Phone number', 'Call 555-123-4567 after six.', '555-123-4567')
    ,@('Phone number', 'Call (555) 123-4567 after six.', '(555) 123-4567')
    ,@('Phone number', 'Phone 020 7946 0123, email later.', '020 7946 0123')
    ,@('Phone number', 'Mobile 07700 900123 works too.', '07700 900123')
    ,@('Phone number', 'Call +44 20 7946 0123 from abroad.', '+44 20 7946 0123')
    ,@('Phone number', 'Bel 06 12345678 na vijf uur.', '06 12345678')
    ,@('Phone number', 'Bel 020 123 4567 op kantoor.', '020 123 4567')
    ,@('Phone number', 'Bel +31 20 123 4567 vanuit het buitenland.', '+31 20 123 4567')
    ,@('IBAN', 'Pay into GB00 DEMO 0000 0000 1234 56 please.', 'GB00 DEMO 0000 0000 1234 56')
    ,@('IBAN', 'Rekening NL91 ABNA 0417 1643 00 graag.', 'NL91 ABNA 0417 1643 00')
    ,@('IBAN', 'Konto DE89 3704 0044 0532 0130 00 bitte.', 'DE89 3704 0044 0532 0130 00')
    ,@('Card number', 'Card 4111 1111 1111 1111 expires soon.', '4111 1111 1111 1111')
    ,@('US Social Security number', 'SSN 123-45-6789 on file.', '123-45-6789')
    ,@('UK postcode', 'Hi, I am Sarah Collins, 8 Willow Road, London W6 0XX.', 'W6 0XX')
    ,@('Name', 'Hi, I am Sarah Collins, 8 Willow Road, London W6 0XX.', 'Sarah Collins')
    ,@('Postal address', 'Hi, I am Sarah Collins, 8 Willow Road, London W6 0XX.', '8 Willow Road')
)

$failures = New-Object 'System.Collections.Generic.List[string]'
foreach ($case in $cases) {
    $category, $text, $expected = $case
    $findings = @($detector.GetMethod('Detect').Invoke($null, @($text)))
    $hit = $findings | Where-Object { $_.Category -eq $category -and $_.Value -eq $expected }
    $isCovered = $covered -contains $category
    $result = if ($hit) { 'detected' } elseif ($isCovered) { 'MISSED' } else { 'not covered' }
    Write-Output ('{0,-26} {1,-12} {2}' -f $category, $result, $expected)
    if ($isCovered -and -not $hit) { $failures.Add("${category}: '$expected' not detected in '$text'") }
    if ($category -eq 'IBAN') {
        foreach ($wrong in $findings | Where-Object { $_.Category -ne 'IBAN' }) {
            $failures.Add("IBAN '$expected' also reported as $($wrong.Category): '$($wrong.Value)'")
        }
    }
}

# The TASK-148 demo card text: every covered detail in it is found.
$demo = "Hi, I am Sarah Collins, 8 Willow Road, London W6 0XX.`r`n" +
    "Phone 020 7946 0123, email sarah.collins@mail.example.`r`n" +
    'Write a complaint to my energy supplier. They took 480 pounds from my account GB00 DEMO 0000 0000 1234 56.'
$demoFindings = @($detector.GetMethod('Detect').Invoke($null, @($demo)))
Write-Output ('Demo card: ' + (($demoFindings | ForEach-Object { "$($_.Category): $($_.Value)" }) -join '; '))
foreach ($value in 'W6 0XX', '020 7946 0123', 'sarah.collins@mail.example', 'GB00 DEMO 0000 0000 1234 56') {
    if (-not ($demoFindings | Where-Object Value -eq $value)) { $failures.Add("Demo card: '$value' not detected") }
}
if ($demoFindings.Count -ne 4) { $failures.Add("Demo card: expected 4 findings, got $($demoFindings.Count)") }

if ($TimingKilobytes -gt 0) {
    # Review time: the demo card repeated to the requested size, one warm-up run, then the median of 21 runs.
    $builder = [Text.StringBuilder]::new()
    while ($builder.Length -lt $TimingKilobytes * 1024) { [void]$builder.Append($demo).Append("`r`n") }
    $card = $builder.ToString()
    $detect = $detector.GetMethod('Detect')
    $warm = @($detect.Invoke($null, @($card)))
    $runs = foreach ($i in 1..21) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        [void]$detect.Invoke($null, @($card))
        $watch.Stop()
        $watch.Elapsed.TotalMilliseconds
    }
    $median = ($runs | Sort-Object)[10]
    Write-Output ('Review time: {0:N1} ms median of 21 runs, {1:N0} characters, {2} findings.' -f $median, $card.Length, $warm.Count)
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "FAIL: $_" }
    exit 1
}
Write-Output "PII detection checks passed. Covered: $($covered -join ', ')."
exit 0
