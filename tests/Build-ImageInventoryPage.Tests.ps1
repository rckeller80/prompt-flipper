#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

BeforeAll {
  $script:ScriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'app/Build-ImageInventoryPage.ps1'
  . $script:ScriptPath

  function New-TestPng {
    param([string]$Path, [int]$Edge)
    $bytes = New-Object byte[] 33
    [byte[]]$signature = 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52
    [Array]::Copy($signature, $bytes, $signature.Length)
    $bytes[18] = [byte](($Edge -shr 8) -band 0xFF); $bytes[19] = [byte]($Edge -band 0xFF)
    $bytes[22] = [byte](($Edge -shr 8) -band 0xFF); $bytes[23] = [byte]($Edge -band 0xFF)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllBytes($Path, $bytes)
  }

  function New-TestIco {
    param([string]$Path, [int[]]$Edges)
    $bytes = New-Object byte[] (6 + 16 * $Edges.Count)
    $bytes[2] = 1; $bytes[4] = [byte]$Edges.Count
    for ($i = 0; $i -lt $Edges.Count; $i++) { $bytes[6 + 16 * $i] = [byte]($Edges[$i] % 256) }
    [IO.File]::WriteAllBytes($Path, $bytes)
  }
}

Describe 'Build-ImageInventoryPage' {
  BeforeEach {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('pf-inventory-' + [guid]::NewGuid().ToString('N'))
    $app = Join-Path $root 'app'
    $assets = Join-Path $root 'artifacts/assets'
    New-Item -ItemType Directory -Force -Path $app | Out-Null
    New-TestPng -Path (Join-Path $assets 'toolbar/folder.png') -Edge 64
    New-TestPng -Path (Join-Path $assets 'toolbar/copy_16.png') -Edge 16
    New-TestPng -Path (Join-Path $assets 'toolbar/copy_32.png') -Edge 32
    New-TestPng -Path (Join-Path $assets 'quick/ok_24.png') -Edge 24
    New-TestPng -Path (Join-Path $assets 'quick/ok_512.png') -Edge 512
    New-TestPng -Path (Join-Path $root 'artifacts/IconPack/Toolbar/20/Exit.png') -Edge 20
    New-TestIco -Path (Join-Path $app 'App.ico') -Edges @(16, 256)
    $csproj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <EmbeddedResource Include="..\artifacts\assets\toolbar\*.png" LogicalName="Assets.%(Filename).png" />
    <EmbeddedResource Include="..\artifacts\assets\quick\*.png" Exclude="..\artifacts\assets\quick\*_512.png" LogicalName="QuickIcons.%(Filename).png" />
    <EmbeddedResource Include="..\artifacts\IconPack\Toolbar\20\Exit.png" LogicalName="Toolbar.20.Exit.png" />
    <EmbeddedResource Include="App.ico" LogicalName="App.ico" />
    <EmbeddedResource Include="Strings.resx" />
  </ItemGroup>
</Project>
'@
    $projectFile = Join-Path $app 'Test.csproj'
    [IO.File]::WriteAllText($projectFile, $csproj)
    $artworkFile = Join-Path $app 'ToolbarArtwork.cs'
    [IO.File]::WriteAllText($artworkFile, 'var m = c switch { "Open" => "folder", "Copy" => "copy", _ => null };')
    $outputPath = Join-Path $root 'artifacts/IconPack/image-inventory.html'
  }

  AfterEach {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
  }

  It 'reads PNG and ICO header sizes' {
    (Get-ImageDimension -Path (Join-Path $assets 'toolbar/folder.png')).Width | Should -Be 64
    $ico = Get-ImageDimension -Path (Join-Path $app 'App.ico')
    $ico.Width | Should -Be 256
    @($ico.Sizes) | Should -Be @(16, 256)
  }

  It 'reads PNG dimensions above the byte range under both PowerShell editions' {
    foreach ($edge in @(256, 512)) {
      $path = Join-Path $assets ('large_' + $edge + '.png')
      New-TestPng -Path $path -Edge $edge
      $dimension = Get-ImageDimension -Path $path
      $dimension.Width | Should -Be $edge
      $dimension.Height | Should -Be $edge
    }
  }

  It 'expands wildcards, applies Exclude and LogicalName, and skips non-images' {
    $names = @(Get-EmbeddedImage -ProjectFile $projectFile | ForEach-Object { $_.LogicalName }) | Sort-Object
    $names | Should -Be @('App.ico', 'Assets.copy_16.png', 'Assets.copy_32.png', 'Assets.folder.png', 'QuickIcons.ok_24.png', 'Toolbar.20.Exit.png')
  }

  It 'maps logical names to a role key and size' {
    $role = Get-ImageRole -LogicalName 'Toolbar.20.Exit.png'
    $role.Key | Should -Be 'Exit'; $role.Size | Should -Be 20
    $role = Get-ImageRole -LogicalName 'QuickIcons.de_sanitize_button_128.png'
    $role.Key | Should -Be 'de_sanitize_button'; $role.Size | Should -Be 128; $role.Kind | Should -Be 'Quick icon'
    (Get-ImageRole -LogicalName 'Assets.text-size.png').Key | Should -Be 'text-size'
    (Get-ImageRole -LogicalName 'PromptFlipper.ico').Kind | Should -Be 'App identity'
  }

  It 'groups every shipped size under one role with the command label from ToolbarArtwork.cs' {
    $inventory = @(Get-ImageInventory -ProjectFile $projectFile -ArtworkFile $artworkFile -OutputDirectory (Split-Path -Parent $outputPath))
    $inventory.Count | Should -Be 5
    $copy = $inventory | Where-Object Key -eq 'copy'
    $copy.Label | Should -Be 'Copy'
    @($copy.Images | ForEach-Object { $_.Size }) | Should -Be @(16, 32)
    $copy.Images[0].Src | Should -Be '../assets/toolbar/copy_16.png'
    ($inventory | Where-Object Key -eq 'folder').Label | Should -Be 'Open'
  }

  It 'writes a page whose embedded data parses and names every role' {
    $result = & $script:ScriptPath -ProjectFile $projectFile -ArtworkFile $artworkFile -OutputPath $outputPath
    $result.Roles | Should -Be 5
    $result.Images | Should -Be 6
    $html = [IO.File]::ReadAllText($outputPath)
    $html | Should -Match '^<!DOCTYPE html>'
    $json = [regex]::Match($html, '(?s)<script id="inventory" type="application/json">(.*?)</script>').Groups[1].Value
    $data = $json | ConvertFrom-Json
    @($data | ForEach-Object { $_.Key }) | Sort-Object | Should -Be @('App', 'copy', 'Exit', 'folder', 'ok')
  }

  It 'throws when the project embeds no images' {
    [IO.File]::WriteAllText($projectFile, '<Project><ItemGroup><EmbeddedResource Include="Strings.resx" /></ItemGroup></Project>')
    { & $script:ScriptPath -ProjectFile $projectFile -ArtworkFile $artworkFile -OutputPath $outputPath } | Should -Throw
  }
}
