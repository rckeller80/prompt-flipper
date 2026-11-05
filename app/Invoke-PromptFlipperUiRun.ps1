<#
.SYNOPSIS
    Runs a live UI check of a Prompt Flipper window and reports what a screen
    reader and a keyboard user get.

.DESCRIPTION
    Opens a Prompt Flipper window in one of two ways and drives it through UI Automation:

    - App mode (-Path to PromptFlipper.exe): starts the app.
      With -OpenControl, it expands the named control when ExpandCollapsePattern is available.
      Otherwise, it focuses the control and presses Enter.
    - Host mode (-Path to PromptFlipper.dll with -HostType): creates that Form type, internal or
      public, by reflection in a separate pwsh -STA process and shows it as a dialog. Use it for a
      dialog that needs state the app does not start with, such as current card text.

    For each Prompt Flipper window or popup, the script prints its UI Automation tree and saves a PNG to -OutputDirectory.
    The tree lists control types, names, access keys, help text, enabled states, focusable states and edit-box text.
    It sends each -Key sequence and reports the focused element and any new windows.
    Common dialogs such as Open appear by name only.

    Edit-box text is read with WM_GETTEXT on the native handle, because a read-only multiline
    WinForms TextBox exposes neither ValuePattern nor TextPattern.

    Execution platform: Windows; PowerShell: Windows PowerShell 5.1 (UI Automation client).
    Host mode also needs pwsh 7 with a .NET runtime that loads the app assembly.

    The app writes its real per-user state - settings and protected sanitizer rules - while it
    runs. A check that adds data must remove it with its own keys, or the caller cleans up after.

.PARAMETER Path
    PromptFlipper.exe for app mode, or PromptFlipper.dll for host mode.

.PARAMETER WindowName
    Title, or title prefix, of the window the check starts in.

.PARAMETER OpenControl
    App mode: accessible name of the main-window control that opens -WindowName.

.PARAMETER HostType
    Host mode: full name of the Form type to show, for example PromptNavigator.SanitizerDialog.

.PARAMETER HostArgument
    Host mode: string constructor arguments for -HostType, in order.

.PARAMETER Key
    SendKeys sequences sent one at a time to the focused window, for example '%n' for Alt+N.

.PARAMETER OutputDirectory
    Folder for the window screenshots. Created if missing.

.EXAMPLE
    powershell.exe -NoProfile -STA -Command "& .\Invoke-PromptFlipperUiRun.ps1 -Path bin\...\PromptFlipper.dll -HostType PromptNavigator.SanitizerDialog -HostArgument 'card text', 'Card' -WindowName 'Sanitizer' -Key '%n', '%s', '%p' -OutputDirectory `$env:TEMP\ui"

    Use -Command, not -File: under -File, powershell.exe passes each token as a string and
    cannot bind the string arrays -HostArgument and -Key.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$WindowName,

    [string]$OpenControl,

    [string]$HostType,

    [string[]]$HostArgument = @(),

    [string[]]$Key = @(),

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
if (-not ('PromptFlipperUiRun.Native' -as [type])) {
    Add-Type -Namespace PromptFlipperUiRun -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, System.Text.StringBuilder lParam);
'@
}

$Auto = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$Type = [System.Windows.Automation.ControlType]
$SkippedTypes = 'Pane', 'ScrollBar', 'Thumb', 'TitleBar', 'MenuBar'

function New-PropertyCondition([System.Windows.Automation.AutomationProperty]$Property, $Value) {
    New-Object System.Windows.Automation.PropertyCondition($Property, $Value)
}

function Get-EditText([System.Windows.Automation.AutomationElement]$Element) {
    $handle = [IntPtr]$Element.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) { return $null }
    $buffer = New-Object System.Text.StringBuilder 65536
    [void][PromptFlipperUiRun.Native]::SendMessage($handle, 0x000D, [IntPtr]65536, $buffer)
    $buffer.ToString()
}

function Get-ProcessWindow([int]$ProcessId) {
    $processCondition = New-PropertyCondition $Auto::ProcessIdProperty $ProcessId
    $condition = New-Object System.Windows.Automation.AndCondition(
        $processCondition,
        (New-PropertyCondition $Auto::ControlTypeProperty $Type::Window))
    $menuCondition = New-Object System.Windows.Automation.AndCondition(
        $processCondition,
        (New-PropertyCondition $Auto::ControlTypeProperty $Type::Menu))
    # ToolStrip popups can be desktop children or menu fragments beneath their opener.
    $windows = @($Auto::RootElement.FindAll($Scope::Children, $processCondition)) +
        @($Auto::RootElement.FindAll($Scope::Descendants, $condition)) +
        @($Auto::RootElement.FindAll($Scope::Descendants, $menuCondition))
    $handles = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($window in $windows) {
        $current = $window.Current
        if ($current.ClassName -eq 'SysShadow' -or $current.ClassName -like '*tooltips_class32*') { continue }
        if ($current.ControlType -eq $Type::Menu -and $current.IsOffscreen) { continue }
        if ($handles.Add($current.NativeWindowHandle)) { $window }
    }
}

function Wait-ProcessWindow([int]$ProcessId, [string]$Name) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $match = Get-ProcessWindow $ProcessId | Where-Object { $_.Current.Name -like "$Name*" } |
            Select-Object -First 1
        if ($match) { return $match }
        Start-Sleep -Milliseconds 300
    }
    throw "No window named '$Name' opened within $TimeoutSeconds seconds."
}

function Write-ElementTree([System.Windows.Automation.AutomationElement]$Window) {
    $all = $Window.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
        $current = $element.Current
        $typeName = $current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        if ($SkippedTypes -contains $typeName) { continue }
        $line = '  {0,-12} name=''{1}'' key=''{2}'' help=''{3}'' enabled={4} focusable={5}' -f `
            $typeName, $current.Name, $current.AccessKey, $current.HelpText, $current.IsEnabled,
            $current.IsKeyboardFocusable
        if ($typeName -eq 'Edit') {
            $text = Get-EditText $element
            if ($null -ne $text) { $line += " text='" + ($text -replace "`r?`n", '\n') + "'" }
        }
        $line
    }
}

function Save-WindowImage([System.Windows.Automation.AutomationElement]$Window, [int]$Index) {
    $bounds = $Window.Current.BoundingRectangle
    $safeName = ($Window.Current.Name -replace '[^A-Za-z0-9]+', '-').Trim('-')
    $file = Join-Path $OutputDirectory ('{0:D2}-{1}.png' -f $Index, $safeName)
    $bitmap = New-Object System.Drawing.Bitmap ([int]$bounds.Width), ([int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, $bitmap.Size)
        $bitmap.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
    "  screenshot: $file"
}

function Write-NewWindow([int]$ProcessId, [System.Collections.Generic.HashSet[int]]$Seen) {
    foreach ($window in Get-ProcessWindow $ProcessId) {
        if (-not $Seen.Add($window.Current.NativeWindowHandle)) { continue }
        if ($window.Current.ControlType -eq $Type::Window -and
            $window.Current.ClassName -notlike 'WindowsForms*') {
            "window opened: '$($window.Current.Name)' (common dialog, tree not listed)"
            continue
        }
        "window opened: '$($window.Current.Name)'"
        Write-ElementTree $window
        $script:ImageIndex++
        Save-WindowImage $window $script:ImageIndex
    }
}

function Get-FocusedDescription {
    $focused = $Auto::FocusedElement
    if (-not $focused) { return '(nothing)' }
    $window = $focused
    while ($window -and $window.Current.ControlType -ne $Type::Window) {
        $window = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($window)
    }
    "'{0}' [{1}] in '{2}'" -f $focused.Current.Name,
        ($focused.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''),
        $(if ($window) { $window.Current.Name } else { '' })
}

# A hosted dialog has no visible owner, so closing a child window can hand the foreground to
# another application. Keys always go to this process: refocus its newest open window first.
function Set-ProcessForeground([int]$ProcessId) {
    $focused = $Auto::FocusedElement
    if ($focused -and $focused.Current.ProcessId -eq $ProcessId) { return }
    $window = Get-ProcessWindow $ProcessId | Where-Object {
        $_.Current.ControlType -eq $Type::Window -and $_.Current.ClassName -like 'WindowsForms*'
    } |
        Select-Object -Last 1
    if (-not $window) { throw 'The process has no open window to send keys to.' }
    $window.SetFocus()
    Start-Sleep -Milliseconds 300
}

function Resolve-UiHost {
    [CmdletBinding()]
    param(
        [scriptblock]$Resolver = {
            & (Join-Path $PSScriptRoot '..\..\..\scripts\Resolve-PowerShell7.ps1')
        }
    )
    $resolved = & $Resolver
    if ($null -eq $resolved) {
        throw 'PowerShell 7 was not found by Resolve-PowerShell7.ps1.'
    }
    if ($resolved.PermissionMode -eq 'Escalated') {
        throw "PowerShell 7 launch requires permission: $($resolved.Path)"
    }
    if ($resolved.PermissionMode -ne 'Direct' -or [string]::IsNullOrWhiteSpace($resolved.Path)) {
        throw 'The PowerShell 7 resolver returned no usable executable.'
    }
    return $resolved.Path
}

$hostMode = [bool]$HostType
if ($hostMode -and $OpenControl) { throw '-OpenControl applies to app mode only; omit it with -HostType.' }
$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$script:ImageIndex = 0

if ($hostMode) {
    $quote = { param($text) "'" + ($text -replace "'", "''") + "'" }
    $argumentList = ($HostArgument | ForEach-Object { & $quote $_ }) -join ', '
    $hostScript = @"
Add-Type -AssemblyName System.Windows.Forms
`$type = [System.Reflection.Assembly]::LoadFrom($(& $quote $resolvedPath)).GetType($(& $quote $HostType), `$true)
[System.Windows.Forms.Application]::EnableVisualStyles()
`$form = [System.Activator]::CreateInstance(`$type, [System.Reflection.BindingFlags]'Instance,Public,NonPublic', `$null, [object[]]@($argumentList), `$null)
`$form.StartPosition = 'CenterScreen'
[void]`$form.ShowDialog()
"@
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($hostScript))
    $hostPath = Resolve-UiHost
    $process = Start-Process -FilePath $hostPath -PassThru -WindowStyle Hidden `
        -ArgumentList '-NoProfile', '-STA', '-EncodedCommand', $encoded
}
else {
    $process = Start-Process -FilePath $resolvedPath -PassThru
}

$seen = New-Object 'System.Collections.Generic.HashSet[int]'
$expandedOpener = $false
try {
    if ($OpenControl) {
        $main = Wait-ProcessWindow $process.Id 'Prompt Flipper'
        [void]$seen.Add($main.Current.NativeWindowHandle)
        $main.SetFocus()
        $opener = $main.FindFirst($Scope::Descendants, (New-PropertyCondition $Auto::NameProperty $OpenControl))
        if (-not $opener) { throw "The main window has no control named '$OpenControl'." }
        $expandPattern = $null
        $expandedOpener = $opener.TryGetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expandPattern)
        if ($expandedOpener) {
            $expandPattern.Expand()
            Start-Sleep -Milliseconds 300
        }
        else {
            $opener.SetFocus()
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        }
    }

    $start = Wait-ProcessWindow $process.Id $WindowName
    Write-NewWindow $process.Id $seen
    # Refocusing the main window would dismiss a menu that Expand() just opened.
    if (-not $expandedOpener) { $start.SetFocus() }
    Start-Sleep -Milliseconds 300
    "focus at start: " + (Get-FocusedDescription)

    foreach ($sequence in $Key) {
        Set-ProcessForeground $process.Id
        [System.Windows.Forms.SendKeys]::SendWait($sequence)
        Start-Sleep -Milliseconds 600
        "key $sequence -> " + (Get-FocusedDescription)
        Write-NewWindow $process.Id $seen
    }
}
finally {
    if (-not $process.HasExited) {
        if ($hostMode -or -not $process.CloseMainWindow() -or -not $process.WaitForExit(5000)) {
            $process.Kill()
        }
    }
}
