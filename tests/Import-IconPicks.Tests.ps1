#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

BeforeAll {
  . (Join-Path (Split-Path -Parent $PSScriptRoot) 'app/Import-IconPicks.ps1')

  function New-ColorPng {
    param([string]$Path, [int]$Width, [int]$Height, [Drawing.Color]$Color)
    $bitmap = New-Object Drawing.Bitmap($Width, $Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
      $graphics.Clear($Color)
      $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
  }
}

Describe 'Import-IconPicks' {
  BeforeEach {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('pf-icon-import-' + [guid]::NewGuid().ToString('N'))
    $app = Join-Path $root 'app'
    $assets = Join-Path $root 'artifacts/assets'
    $pack = Join-Path $root 'artifacts/IconPack'
    $masters = Join-Path $pack 'App'
    $sources = Join-Path $pack 'SelectedSources'
    foreach ($directory in @($app, $assets, $masters, $sources)) {
      New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $selectedPath = Join-Path $assets 'search.png'
    $unpickedPath = Join-Path $assets 'copy.png'
    New-ColorPng -Path $selectedPath -Width 64 -Height 64 -Color Red
    New-ColorPng -Path $unpickedPath -Width 16 -Height 16 -Color Green
    New-ColorPng -Path (Join-Path $sources 'selected-search.png') -Width 64 -Height 32 -Color Blue
    New-ColorPng -Path (Join-Path $sources 'selected-app.png') -Width 64 -Height 64 -Color Gold
    New-ColorPng -Path (Join-Path $masters 'PromptFlipper_512.png') -Width 512 -Height 512 -Color Red
    $source = [Drawing.Image]::FromFile($selectedPath)
    try { $oldIcon = ConvertTo-IconFile -Source $source -Sizes @(16, 32, 256) } finally { $source.Dispose() }
    foreach ($path in @((Join-Path $app 'PromptFlipper.ico'), (Join-Path $masters 'PromptFlipper.ico'), (Join-Path $pack 'PromptFlipper.ico'))) {
      [IO.File]::WriteAllBytes($path, $oldIcon)
    }
    $projectFile = Join-Path $app 'PromptFlipper.csproj'
    $projectText = @'
<Project><ItemGroup>
  <EmbeddedResource Include="../artifacts/assets/search.png" LogicalName="Assets.search.png" />
  <EmbeddedResource Include="../artifacts/assets/copy.png" LogicalName="Assets.copy.png" />
  <EmbeddedResource Include="PromptFlipper.ico" LogicalName="PromptFlipper.ico" />
</ItemGroup></Project>
'@
    [IO.File]::WriteAllText($projectFile, $projectText)
    $picksFile = Join-Path $pack 'icon-picks.json'
    $export = [pscustomobject]@{ roles = @(
      [pscustomobject]@{ key = 'search'; kind = 'Toolbar'; current = @('Assets.search.png'); picks = @([pscustomobject]@{ id = 'selected-search' }) },
      [pscustomobject]@{ key = 'copy'; kind = 'Toolbar'; current = @('Assets.copy.png'); picks = @() },
      [pscustomobject]@{ key = 'PromptFlipper'; kind = 'App identity'; current = @('PromptFlipper.ico'); picks = @([pscustomobject]@{ id = 'selected-app' }) }
    ) }
    [IO.File]::WriteAllText($picksFile, (ConvertTo-Json -InputObject $export -Depth 6))
    $unpickedHash = (Get-FileHash -LiteralPath $unpickedPath).Hash
    $exportHash = (Get-FileHash -LiteralPath $picksFile).Hash
    $selectedHash = (Get-FileHash -LiteralPath $selectedPath).Hash
  }

  AfterEach {
    $resolved = [IO.Path]::GetFullPath($root)
    if ($resolved.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('pf-icon-import-', [StringComparison]::Ordinal)) {
      Remove-Item -LiteralPath $resolved -Recurse -Force
    }
  }

  It 'applies selected roles offline and preserves unpicked files and the export' {
    $result = Import-IconPicks -ProjectFile $projectFile -PicksFile $picksFile -SourceDirectory $sources
    $result.SelectedRoles | Should -Be 2
    $result.PreservedRoles | Should -Be 1
    $result.WrittenFiles | Should -Be 5
    (Get-FileHash -LiteralPath $unpickedPath).Hash | Should -Be $unpickedHash
    (Get-FileHash -LiteralPath $picksFile).Hash | Should -Be $exportHash
    (Get-FileHash -LiteralPath $selectedPath).Hash | Should -Not -Be $selectedHash
    $bitmap = [Drawing.Bitmap]::FromFile($selectedPath)
    try {
      $bitmap.Width | Should -Be 64
      $bitmap.Height | Should -Be 64
      $bitmap.GetPixel(0, 0).A | Should -Be 0
      $bitmap.GetPixel(32, 32).B | Should -Be 255
    } finally { $bitmap.Dispose() }
  }

  It 'keeps every ICO frame and synchronizes all application icon copies' {
    Import-IconPicks -ProjectFile $projectFile -PicksFile $picksFile -SourceDirectory $sources | Out-Null
    $iconPath = Join-Path $app 'PromptFlipper.ico'
    @(Get-ImageDimension -Path $iconPath).Sizes | Should -Be @(16, 32, 256)
    $hash = (Get-FileHash -LiteralPath $iconPath).Hash
    (Get-FileHash -LiteralPath (Join-Path $masters 'PromptFlipper.ico')).Hash | Should -Be $hash
    (Get-FileHash -LiteralPath (Join-Path $pack 'PromptFlipper.ico')).Hash | Should -Be $hash
    $icon = New-Object Drawing.Icon($iconPath)
    try { $icon.Width | Should -BeGreaterThan 0 } finally { $icon.Dispose() }
    $bytes = [IO.File]::ReadAllBytes($iconPath)
    $edges = @(16, 32, 256)
    for ($i = 0; $i -lt $edges.Count; $i++) {
      $length = [BitConverter]::ToUInt32($bytes, 6 + 16 * $i + 8)
      $offset = [BitConverter]::ToUInt32($bytes, 6 + 16 * $i + 12)
      $payload = New-Object byte[] $length
      [Array]::Copy($bytes, $offset, $payload, 0, $length)
      $stream = New-Object IO.MemoryStream(,$payload)
      $frame = [Drawing.Image]::FromStream($stream)
      try {
        $frame.Width | Should -Be $edges[$i]
        $frame.Height | Should -Be $edges[$i]
      } finally { $frame.Dispose(); $stream.Dispose() }
    }
    (Get-ImageDimension -Path (Join-Path $masters 'PromptFlipper_512.png')).Width | Should -Be 512
  }

  It 'produces identical artwork when the same picks are applied again' {
    Import-IconPicks -ProjectFile $projectFile -PicksFile $picksFile -SourceDirectory $sources | Out-Null
    $first = (Get-FileHash -LiteralPath $selectedPath).Hash
    Import-IconPicks -ProjectFile $projectFile -PicksFile $picksFile -SourceDirectory $sources | Out-Null
    (Get-FileHash -LiteralPath $selectedPath).Hash | Should -Be $first
  }

  It 'rejects ambiguous picks before changing an image' {
    $export.roles[0].picks += [pscustomobject]@{ id = 'another' }
    [IO.File]::WriteAllText($picksFile, (ConvertTo-Json -InputObject $export -Depth 6))
    { Import-IconPicks -ProjectFile $projectFile -PicksFile $picksFile -SourceDirectory $sources } | Should -Throw '*Choose one*'
    (Get-FileHash -LiteralPath $selectedPath).Hash | Should -Be $selectedHash
  }

  It 'rejects stale resource names before changing an image' {
    $export.roles[0].current = @('Assets.obsolete.png')
    [IO.File]::WriteAllText($picksFile, (ConvertTo-Json -InputObject $export -Depth 6))
    { Import-IconPicks -ProjectFile $projectFile -PicksFile $picksFile -SourceDirectory $sources } | Should -Throw '*Stale resource list*'
    (Get-FileHash -LiteralPath $selectedPath).Hash | Should -Be $selectedHash
  }

  It 'renders all sources before writing any application artwork' {
    [IO.File]::WriteAllText((Join-Path $sources 'selected-app.png'), 'invalid source')
    { Import-IconPicks -ProjectFile $projectFile -PicksFile $picksFile -SourceDirectory $sources } | Should -Throw
    (Get-FileHash -LiteralPath $selectedPath).Hash | Should -Be $selectedHash
    (Get-FileHash -LiteralPath $unpickedPath).Hash | Should -Be $unpickedHash
  }
}
