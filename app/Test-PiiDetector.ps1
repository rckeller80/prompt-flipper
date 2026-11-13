#Requires -Version 7.4
<#
.SYNOPSIS
Checks PII detection against fictional fixtures.

.DESCRIPTION
Loads the built PromptFlipper.dll and runs PiiDetector.Detect on one positive and one negative
fixture per recognizer. A positive fixture passes when the detector reports its category for the
expected value. A negative fixture is a near-valid value, such as a wrong check digit or a missing
context word, and passes when the detector does not report its category. A name and a street
address are reported as not covered and do not fail. The check also runs the demo card and
measures Review time on a large card.
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
$detect = $detector.GetMethod('Detect')
$covered = @($detector.GetProperty('CoveredCategories').GetValue($null))

# Category, fixture text, expected value. A positive fixture expects the value to be reported in
# that category; a negative fixture (value prefixed with '!') expects it not to be.
$cases = @(
    ,@('IBAN', 'Pay into GB82 WEST 1234 5698 7654 32 please.', 'GB82 WEST 1234 5698 7654 32')
    ,@('IBAN', 'Rekening NL91 ABNA 0417 1643 00 graag.', 'NL91 ABNA 0417 1643 00')
    ,@('IBAN', 'Pay into GB00 DEMO 0000 0000 1234 56 please.', '!GB00 DEMO 0000 0000 1234 56')
    ,@('Card number', 'Card 4111 1111 1111 1111 expires soon.', '4111 1111 1111 1111')
    ,@('Card number', 'Card 4111 1111 1111 1112 expires soon.', '!4111 1111 1111 1112')
    ,@('Email address', 'Write to sarah.collins@example.com today.', 'sarah.collins@example.com')
    ,@('Email address', 'Write to sarah.collins@localhost today.', '!sarah.collins@localhost')
    ,@('UK NHS number', 'My NHS number is 943 476 5919.', '943 476 5919')
    ,@('UK NHS number', 'My NHS number is 943 476 5918.', '!943 476 5918')
    ,@('UK NHS number', 'Reference 943 476 5919 attached.', '!943 476 5919')
    ,@('NL BSN', 'Mijn BSN is 111222333.', '111222333')
    ,@('NL BSN', 'Mijn BSN is 111222334.', '!111222334')
    ,@('US Social Security number', 'SSN 123-45-6789 on file.', '123-45-6789')
    ,@('US Social Security number', 'SSN 666-45-6789 on file.', '!666-45-6789')
    ,@('UK National Insurance number', 'NI number AB 12 34 56 C.', 'AB 12 34 56 C')
    ,@('UK National Insurance number', 'NI number QQ 12 34 56 C.', '!QQ 12 34 56 C')
    ,@('IP address', 'The server is 192.168.10.24 today.', '192.168.10.24')
    ,@('IP address', 'The value 192.168.10.256 is out of range.', '!192.168.10.256')
    ,@('IP address', 'Connect to 2001:db8::8a2e:370:7334 now.', '2001:db8::8a2e:370:7334')
    ,@('IP address', 'The meeting is at 10:30:00 today.', '!10:30:00')
    ,@('Phone number', 'Call 555-123-4567 after six.', '555-123-4567')
    ,@('Phone number', 'Call (555) 123-4567 after six.', '(555) 123-4567')
    ,@('Phone number', 'Phone 020 7946 0123, email later.', '020 7946 0123')
    ,@('Phone number', 'Mobile 07700 900123 works too.', '07700 900123')
    ,@('Phone number', 'Call +44 20 7946 0123 from abroad.', '+44 20 7946 0123')
    ,@('Phone number', 'Bel 06 12345678 na vijf uur.', '06 12345678')
    ,@('Phone number', 'Bel +31 20 123 4567 vanuit het buitenland.', '+31 20 123 4567')
    ,@('Phone number', 'Order 06 1234 shipped.', '!06 1234')
    ,@('UK postcode', 'Hi, I am Sarah Collins, 8 Willow Road, London W6 0XX.', 'W6 0XX')
    ,@('UK postcode', 'The model W6 0XX ships next week.', '!W6 0XX')
    ,@('NL postcode', 'Adres: Damstraat 1, 1012 JS Amsterdam.', '1012 JS')
    ,@('NL postcode', 'Adres: Damstraat 1, 1012 SS Amsterdam.', '!1012 SS')
    ,@('Name', 'Hi, I am Sarah Collins, 8 Willow Road, London W6 0XX.', 'Sarah Collins')
    ,@('Postal address', 'Hi, I am Sarah Collins, 8 Willow Road, London W6 0XX.', '8 Willow Road')
)

$failures = New-Object 'System.Collections.Generic.List[string]'
$tested = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($case in $cases) {
    $category, $text, $expected = $case
    $negative = $expected.StartsWith('!')
    $value = $expected.TrimStart('!')
    $findings = @($detect.Invoke($null, @($text)))
    $hit = $findings | Where-Object { $_.Category -eq $category -and $_.Value -eq $value }
    $isCovered = $covered -contains $category
    if (-not $isCovered) {
        Write-Output ('{0,-30} {1,-12} {2}' -f $category, 'not covered', $value)
        continue
    }
    [void]$tested.Add("$category|$([int]$negative)")
    $result = if ($negative) { if ($hit) { 'REPORTED' } else { 'rejected' } } else { if ($hit) { 'detected' } else { 'MISSED' } }
    Write-Output ('{0,-30} {1,-12} {2}' -f $category, $result, $value)
    if ($negative -and $hit) { $failures.Add("${category}: '$value' reported in '$text'") }
    if (-not $negative -and -not $hit) { $failures.Add("${category}: '$value' not detected in '$text'") }
}

# Every covered category has at least one positive and one negative fixture.
foreach ($category in $covered) {
    foreach ($kind in 0, 1) {
        if (-not $tested.Contains("$category|$kind")) {
            $failures.Add("${category}: no $(if ($kind) { 'negative' } else { 'positive' }) fixture")
        }
    }
}

# The demo card text: every covered detail in it is found, and nothing else.
$demo = "Hi, I am Sarah Collins, 8 Willow Road, London W6 0XX.`r`n" +
    "Phone 020 7946 0123, email sarah.collins@mail.example.`r`n" +
    'Write a complaint to my energy supplier. They took 480 pounds from my account GB82 WEST 1234 5698 7654 32.'
$demoFindings = @($detect.Invoke($null, @($demo)))
Write-Output ('Demo card: ' + (($demoFindings | ForEach-Object { "$($_.Category): $($_.Value)" }) -join '; '))
foreach ($value in 'W6 0XX', '020 7946 0123', 'sarah.collins@mail.example', 'GB82 WEST 1234 5698 7654 32') {
    if (-not ($demoFindings | Where-Object Value -eq $value)) { $failures.Add("Demo card: '$value' not detected") }
}
if ($demoFindings.Count -ne 4) { $failures.Add("Demo card: expected 4 findings, got $($demoFindings.Count)") }

if ($TimingKilobytes -gt 0) {
    # Review time: the demo card repeated to the requested size, one warm-up run, then the median of 21 runs.
    $builder = [Text.StringBuilder]::new()
    while ($builder.Length -lt $TimingKilobytes * 1024) { [void]$builder.Append($demo).Append("`r`n") }
    $card = $builder.ToString()
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
