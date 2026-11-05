<#
.SYNOPSIS
Applies the selected artwork to existing embedded image files.

.DESCRIPTION
Reads the comparison export and preserves roles without a selection.
Downloads selected sources once and renders each existing size from its source.
Updates the application icon master and its copies together.
Execution platform: Windows; PowerShell: Windows PowerShell 5.1 or PowerShell 7.

.PARAMETER ProjectFile
Path to the application project. Defaults to PromptFlipper.csproj beside this script.

.PARAMETER PicksFile
Path to the comparison export. Defaults to artifacts/IconPack/icon-picks.json.

.PARAMETER SourceDirectory
Directory for downloaded PNG sources. Existing sources permit offline regeneration.

.EXAMPLE
.\Import-IconPicks.ps1
Applies the saved selections and preserves unpicked artwork.
#>
[CmdletBinding()]
param([string]$ProjectFile, [string]$PicksFile, [string]$SourceDirectory)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build-ImageInventoryPage.ps1')
Add-Type -AssemblyName System.Drawing

function ConvertTo-IconPng {
  param([Drawing.Image]$Source, [int]$Width, [int]$Height)
  $bitmap = New-Object Drawing.Bitmap($Width, $Height)
  $graphics = [Drawing.Graphics]::FromImage($bitmap)
  $stream = New-Object IO.MemoryStream
  try {
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $scale = [Math]::Min($Width / $Source.Width, $Height / $Source.Height)
    $drawWidth = [int][Math]::Round($Source.Width * $scale)
    $drawHeight = [int][Math]::Round($Source.Height * $scale)
    $rectangle = New-Object Drawing.Rectangle(([int](($Width - $drawWidth) / 2)), ([int](($Height - $drawHeight) / 2)), $drawWidth, $drawHeight)
    $graphics.DrawImage($Source, $rectangle)
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    return ,$stream.ToArray()
  } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}

function ConvertTo-IconFile {
  param([Drawing.Image]$Source, [int[]]$Sizes)
  $stream = New-Object IO.MemoryStream
  $writer = New-Object IO.BinaryWriter($stream)
  try {
    $frames = @($Sizes | ForEach-Object { ,(ConvertTo-IconPng -Source $Source -Width $_ -Height $_) })
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$Sizes.Count)
    $offset = 6 + 16 * $Sizes.Count
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
      $edge = if ($Sizes[$i] -eq 256) { 0 } else { $Sizes[$i] }
      $writer.Write([byte]$edge); $writer.Write([byte]$edge)
      $writer.Write([byte]0); $writer.Write([byte]0)
      $writer.Write([uint16]1); $writer.Write([uint16]32)
      $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
      $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    $writer.Flush()
    return ,$stream.ToArray()
  } finally { $writer.Dispose(); $stream.Dispose() }
}

function Import-IconPicks {
  param([string]$ProjectFile, [string]$PicksFile, [string]$SourceDirectory)
  $projectPath = (Resolve-Path -LiteralPath $ProjectFile).ProviderPath
  $appDirectory = Split-Path -Parent $projectPath
  $projectDirectory = Split-Path -Parent $appDirectory
  $iconDirectory = Join-Path $projectDirectory 'artifacts/IconPack'
  $export = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Resolve-Path -LiteralPath $PicksFile).ProviderPath))
  $images = @(Get-EmbeddedImage -ProjectFile $projectPath)
  $roles = @{}
  foreach ($image in $images) {
    $key = (Get-ImageRole -LogicalName $image.LogicalName).Key
    if (-not $roles.ContainsKey($key)) { $roles[$key] = @() }
    $roles[$key] += $image
  }
  $seen = @{}
  $selected = @()
  foreach ($role in $export.roles) {
    if ($seen.ContainsKey($role.key) -or -not $roles.ContainsKey($role.key)) { throw ('Unknown or duplicate role: ' + $role.key) }
    $seen[$role.key] = $true
    if (Compare-Object -ReferenceObject @($roles[$role.key].LogicalName) -DifferenceObject @($role.current)) { throw ('Stale resource list: ' + $role.key) }
    if (@($role.picks).Count -gt 1) { throw ('Choose one candidate for ' + $role.key) }
    if (@($role.picks).Count -eq 0) { continue }
    if ($role.picks[0].id -notmatch '^[A-Za-z0-9_-]+$') { throw ('Invalid icon ID: ' + $role.key) }
    $selected += $role
  }
  if ($seen.Count -ne $roles.Count) { throw 'The export does not cover every embedded role.' }
  $unpicked = @{}
  foreach ($key in $roles.Keys) {
    if ($key -in @($selected | ForEach-Object { $_.key })) { continue }
    foreach ($image in $roles[$key]) { $unpicked[$image.SourcePath] = (Get-FileHash -LiteralPath $image.SourcePath -Algorithm SHA256).Hash }
  }
  New-Item -ItemType Directory -Force -Path $SourceDirectory | Out-Null
  $pending = New-Object 'System.Collections.Generic.List[object]'
  foreach ($role in $selected) {
    $sourcePath = Join-Path $SourceDirectory ($role.picks[0].id + '.png')
    if (-not (Test-Path -LiteralPath $sourcePath)) {
      $url = 'https://img.icons8.com/?size=512&id=' + [Uri]::EscapeDataString($role.picks[0].id) + '&format=png'
      Invoke-WebRequest -Uri $url -OutFile $sourcePath -UseBasicParsing -TimeoutSec 30
    }
    $source = [Drawing.Image]::FromFile([IO.Path]::GetFullPath($sourcePath))
    try {
      $targets = @($roles[$role.key] | ForEach-Object { $_.SourcePath })
      if ($role.key -eq 'PromptFlipper') {
        $targets += @(Get-ChildItem -LiteralPath (Join-Path $iconDirectory 'App') -File | Where-Object { $_.Name -match '^PromptFlipper(?:_\d+)?\.(png|ico)$' } | ForEach-Object { $_.FullName })
        $copy = Join-Path $iconDirectory 'PromptFlipper.ico'
        if (Test-Path -LiteralPath $copy) { $targets += $copy }
      }
      if ($role.kind -eq 'Quick icon') {
        $directory = Split-Path -Parent $targets[0]
        $namePattern = '^' + [regex]::Escape($role.key) + '(?:_\d+)?\.(png|ico)$'
        $targets += @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match $namePattern } | ForEach-Object { $_.FullName })
      }
      if ($role.key -eq 'Exit') {
        $targets += @(Get-ChildItem -LiteralPath (Join-Path $iconDirectory 'Toolbar') -Filter Exit.png -Recurse -File | ForEach-Object { $_.FullName })
      }
      foreach ($path in @($targets | Sort-Object -Unique)) {
        $dimension = Get-ImageDimension -Path $path
        $bytes = if ([IO.Path]::GetExtension($path) -eq '.ico') {
          ConvertTo-IconFile -Source $source -Sizes $dimension.Sizes
        } else {
          ConvertTo-IconPng -Source $source -Width $dimension.Width -Height $dimension.Height
        }
        $pending.Add([pscustomobject]@{ Path = $path; Bytes = [byte[]]$bytes; Role = $role.key })
      }
    } finally { $source.Dispose() }
  }
  foreach ($item in $pending) { [IO.File]::WriteAllBytes($item.Path, $item.Bytes) }
  foreach ($path in $unpicked.Keys) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $unpicked[$path]) { throw ('Unpicked image changed: ' + $path) }
  }
  [pscustomobject]@{ SelectedRoles = $selected.Count; PreservedRoles = $roles.Count - $selected.Count; WrittenFiles = $pending.Count; PreservedFiles = $unpicked.Count }
}

if ($MyInvocation.InvocationName -ne '.') {
  $projectDirectory = Split-Path -Parent $PSScriptRoot
  if (-not $ProjectFile) { $ProjectFile = Join-Path $PSScriptRoot 'PromptFlipper.csproj' }
  if (-not $PicksFile) { $PicksFile = Join-Path $projectDirectory 'artifacts/IconPack/icon-picks.json' }
  if (-not $SourceDirectory) { $SourceDirectory = Join-Path $projectDirectory 'artifacts/IconPack/SelectedSources' }
  Import-IconPicks -ProjectFile $ProjectFile -PicksFile $PicksFile -SourceDirectory $SourceDirectory
}
