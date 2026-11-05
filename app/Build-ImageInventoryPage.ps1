<#
.SYNOPSIS
Builds the visual image inventory page for Prompt Flipper.

.DESCRIPTION
Reads every EmbeddedResource image in PromptFlipper.csproj, groups the shipped sizes of
each image by role, and writes a self-contained HTML page. The page shows each role's
current artwork at every shipped size beside a live strip of icons8 replacement
candidates, so current and candidate icons are compared by eye rather than by name.

Role labels come from the command-to-asset map in ToolbarArtwork.cs. Candidate search
runs in the browser against the public icons8 search API at view time; this script makes
no network calls. Picks made in the page are kept in the browser and exported as JSON.

Execution platform: any; PowerShell: Windows PowerShell 5.1 or PowerShell 7 (portable
controller).

.PARAMETER ProjectFile
Path to PromptFlipper.csproj. Defaults to the csproj beside this script.

.PARAMETER ArtworkFile
Path to ToolbarArtwork.cs, read for command labels. Defaults to the file beside this script.

.PARAMETER OutputPath
Path of the HTML page to write. Defaults to artifacts/IconPack/image-inventory.html.

.EXAMPLE
.\Build-ImageInventoryPage.ps1
Rebuilds artifacts/IconPack/image-inventory.html from the current csproj.
#>
[CmdletBinding()]
param(
  [string]$ProjectFile,
  [string]$ArtworkFile,
  [string]$OutputPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Default search term per image key. The page lets the owner edit each term; this only
# sets the starting point. Keys not listed search on their own name.
$script:DefaultSearchTerm = @{
  'folder' = 'folder'; 'save' = 'save'; 'search' = 'search'; 'text-size' = 'font size'
  'sanitize' = 'broom'; 'promptflipper_logo' = 'dolphin'; 'previous' = 'back'
  'next' = 'forward'; 'wrap' = 'word wrap'; 'print' = 'print'; 'help' = 'help'
  'copy' = 'copy'; 'delete' = 'trash'; 'settings' = 'settings'; 'Exit' = 'exit'
  'Support' = 'coffee'; 'add' = 'add'; 'PromptFlipper' = 'dolphin'
  'sanitize_button' = 'broom'; 'de_sanitize_button' = 'undo'
  'sanitized_message' = 'ok'; 'de_sanitized_message' = 'restore'
}

# Labels for keys the ToolbarArtwork.cs command map does not name.
$script:RoleLabel = @{
  'wrap' = 'Wrap (toggle)'; 'PromptFlipper' = 'App icon (window, taskbar, EXE)'
  'add' = 'add (shipped, no code loads it)'
  'sanitize_button' = 'Sanitize quick button'; 'de_sanitize_button' = 'De-sanitize quick button'
  'sanitized_message' = 'Sanitized confirmation art'; 'de_sanitized_message' = 'De-sanitized confirmation art'
}

function Get-ImageDimension {
  # Returns the pixel sizes stored in a PNG or ICO header, without loading an image API.
  [CmdletBinding()]
  param([Parameter(Mandatory)][string]$Path)

  $bytes = [IO.File]::ReadAllBytes($Path)
  $sizes = New-Object System.Collections.Generic.List[int]
  $isPng = $bytes.Length -ge 24 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47
  if ($isPng) {
    $width = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor ([int]$bytes[18] -shl 8) -bor [int]$bytes[19]
    $height = ([int]$bytes[20] -shl 24) -bor ([int]$bytes[21] -shl 16) -bor ([int]$bytes[22] -shl 8) -bor [int]$bytes[23]
    return [pscustomobject]@{ Width = $width; Height = $height; Sizes = @($width) }
  }
  $isIco = $bytes.Length -ge 6 -and $bytes[0] -eq 0 -and $bytes[1] -eq 0 -and $bytes[2] -eq 1 -and $bytes[3] -eq 0
  if ($isIco) {
    $count = $bytes[4] -bor ($bytes[5] -shl 8)
    for ($i = 0; $i -lt $count; $i++) {
      $offset = 6 + (16 * $i)
      if ($offset -ge $bytes.Length) { break }
      $edge = [int]$bytes[$offset]
      if ($edge -eq 0) { $edge = 256 }
      $sizes.Add($edge)
    }
    $ordered = @($sizes | Sort-Object -Unique)
    $largest = if ($ordered.Count -gt 0) { $ordered[-1] } else { $null }
    return [pscustomobject]@{ Width = $largest; Height = $largest; Sizes = $ordered }
  }
  return [pscustomobject]@{ Width = $null; Height = $null; Sizes = @() }
}

function Get-RelativeUrl {
  # Relative, URL-escaped path from a directory to a file, for an HTML src attribute.
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$FromDirectory,
    [Parameter(Mandatory)][string]$ToFile
  )
  $separator = [IO.Path]::DirectorySeparatorChar
  $baseText = [IO.Path]::GetFullPath($FromDirectory).TrimEnd($separator) + $separator
  $base = New-Object Uri($baseText)
  $target = New-Object Uri([IO.Path]::GetFullPath($ToFile))
  return $base.MakeRelativeUri($target).ToString()
}

function Get-EmbeddedImage {
  # Expands every EmbeddedResource image item of a csproj into one object per shipped file.
  [CmdletBinding()]
  param([Parameter(Mandatory)][string]$ProjectFile)

  $projectPath = (Resolve-Path -LiteralPath $ProjectFile).ProviderPath
  $projectDir = Split-Path -Parent $projectPath
  [xml]$xml = [IO.File]::ReadAllText($projectPath)
  $separator = [IO.Path]::DirectorySeparatorChar

  $resolve = {
    param([string]$Pattern)
    $normalized = $Pattern.Trim().Replace('\', $separator).Replace('/', $separator)
    if (-not $normalized) { return @() }
    $full = Join-Path $projectDir $normalized
    $directory = Split-Path -Parent $full
    $leaf = Split-Path -Leaf $full
    if (-not (Test-Path -LiteralPath $directory)) { return @() }
    @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -like $leaf } |
      ForEach-Object { $_.FullName })
  }

  foreach ($item in @($xml.SelectNodes('//*[local-name()="EmbeddedResource"]'))) {
    $include = $item.GetAttribute('Include')
    $exclude = $item.GetAttribute('Exclude')
    $logical = $item.GetAttribute('LogicalName')
    $excluded = @{}
    foreach ($pattern in ($exclude -split ';')) {
      foreach ($path in (& $resolve $pattern)) { $excluded[$path] = $true }
    }
    foreach ($pattern in ($include -split ';')) {
      foreach ($path in (& $resolve $pattern)) {
        if ($excluded.ContainsKey($path)) { continue }
        $extension = [IO.Path]::GetExtension($path)
        if ($extension -notin @('.png', '.ico')) { continue }
        $baseName = [IO.Path]::GetFileNameWithoutExtension($path)
        $name = if ($logical) {
          $logical.Replace('%(Filename)', $baseName).Replace('%(Extension)', $extension)
        } else { [IO.Path]::GetFileName($path) }
        $dimension = Get-ImageDimension -Path $path
        [pscustomobject]@{
          LogicalName = $name
          SourcePath  = $path
          Width       = $dimension.Width
          Height      = $dimension.Height
          Sizes       = $dimension.Sizes
        }
      }
    }
  }
}

function Get-ImageRole {
  # Maps a logical resource name to the role key its sizes share, plus the size it ships at.
  [CmdletBinding()]
  param([Parameter(Mandatory)][string]$LogicalName)

  if ($LogicalName -match '^Toolbar\.(\d+)\.(.+)\.png$') {
    return [pscustomobject]@{ Key = $Matches[2]; Size = [int]$Matches[1]; Kind = 'Toolbar (generated set)' }
  }
  if ($LogicalName -match '^(Assets|QuickIcons)\.(.+?)_(\d+)\.png$') {
    $kind = if ($Matches[1] -eq 'QuickIcons') { 'Quick icon' } else { 'Toolbar' }
    return [pscustomobject]@{ Key = $Matches[2]; Size = [int]$Matches[3]; Kind = $kind }
  }
  if ($LogicalName -match '^(Assets|QuickIcons)\.(.+)\.png$') {
    $kind = if ($Matches[1] -eq 'QuickIcons') { 'Quick icon' } else { 'Toolbar' }
    return [pscustomobject]@{ Key = $Matches[2]; Size = $null; Kind = $kind }
  }
  $key = [IO.Path]::GetFileNameWithoutExtension($LogicalName)
  return [pscustomobject]@{ Key = $key; Size = $null; Kind = 'App identity' }
}

function Get-CommandLabel {
  # Reads the "Command" => "asset" pairs from ToolbarArtwork.cs as an asset-to-command map.
  [CmdletBinding()]
  param([string]$ArtworkFile)

  $map = @{}
  if (-not $ArtworkFile -or -not (Test-Path -LiteralPath $ArtworkFile)) { return $map }
  $text = [IO.File]::ReadAllText($ArtworkFile)
  foreach ($match in [regex]::Matches($text, '"(\w+)"\s*=>\s*"([\w-]+)"')) {
    $map[$match.Groups[2].Value] = $match.Groups[1].Value
  }
  return $map
}

function Get-ImageInventory {
  # Groups embedded images by role, one entry per role with every shipped size.
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$ProjectFile,
    [string]$ArtworkFile,
    [Parameter(Mandatory)][string]$OutputDirectory
  )

  $labels = Get-CommandLabel -ArtworkFile $ArtworkFile
  $groups = [ordered]@{}
  foreach ($image in @(Get-EmbeddedImage -ProjectFile $ProjectFile)) {
    $role = Get-ImageRole -LogicalName $image.LogicalName
    if (-not $groups.Contains($role.Key)) {
      $label = if ($labels.ContainsKey($role.Key)) { $labels[$role.Key] }
        elseif ($script:RoleLabel.ContainsKey($role.Key)) { $script:RoleLabel[$role.Key] }
        else { $role.Key }
      $term = if ($script:DefaultSearchTerm.ContainsKey($role.Key)) { $script:DefaultSearchTerm[$role.Key] }
        else { ($role.Key -replace '[-_]+', ' ').Trim() }
      $groups[$role.Key] = [pscustomobject]@{
        Key = $role.Key; Label = $label; Kind = $role.Kind; SearchTerm = $term
        Images = New-Object System.Collections.Generic.List[object]
      }
    }
    $size = if ($null -ne $role.Size) { $role.Size } else { $image.Width }
    $groups[$role.Key].Images.Add([pscustomobject]@{
      LogicalName = $image.LogicalName
      Src         = Get-RelativeUrl -FromDirectory $OutputDirectory -ToFile $image.SourcePath
      Size        = $size
      Width       = $image.Width
      Height      = $image.Height
      Sizes       = @($image.Sizes)
    })
  }
  foreach ($group in $groups.Values) {
    [pscustomobject]@{
      Key = $group.Key; Label = $group.Label; Kind = $group.Kind; SearchTerm = $group.SearchTerm
      Images = @($group.Images | Sort-Object -Property @{ Expression = { [int]$_.Size } }, LogicalName)
    }
  }
}

function ConvertTo-InventoryHtml {
  # Renders the inventory as one self-contained HTML page.
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][object[]]$Inventory,
    [Parameter(Mandatory)][string]$ProjectLabel
  )
  $json = ConvertTo-Json -InputObject @($Inventory) -Depth 6 -Compress
  # Keep the data block from closing its own script element.
  $json = $json.Replace('</', '<\/')
  $template = $script:PageTemplate
  return $template.Replace('__INVENTORY_JSON__', $json).Replace('__PROJECT_LABEL__', $ProjectLabel)
}

$script:PageTemplate = @'
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Prompt Flipper - image inventory</title>
<style>
  :root { --line:#d6d6d6; --muted:#5f5f5f; --accent:#0078d4; --pick:#107c10; --bg:#f5f5f5; }
  * { box-sizing:border-box; }
  body { margin:0; font:14px/1.4 "Segoe UI", system-ui, sans-serif; color:#1b1b1b; background:var(--bg); }
  header { position:sticky; top:0; z-index:5; background:#fff; border-bottom:1px solid var(--line); padding:10px 16px; }
  header h1 { font-size:18px; margin:0 0 6px; font-weight:600; }
  .controls { display:flex; flex-wrap:wrap; gap:12px; align-items:center; }
  .controls label { color:var(--muted); }
  select, input[type=text], button { font:inherit; padding:3px 8px; border:1px solid #a0a0a0; border-radius:4px; background:#fff; }
  button { cursor:pointer; }
  button.primary { background:var(--accent); color:#fff; border-color:var(--accent); }
  .strip { display:grid; grid-template-columns:90px 1fr; gap:4px 8px; align-items:center; margin-top:8px; }
  .strip .row { display:flex; gap:6px; flex-wrap:wrap; align-items:center; min-height:26px; }
  .strip .slot { width:24px; height:24px; display:flex; align-items:center; justify-content:center; border:1px dashed transparent; }
  .strip .slot.empty { border-color:#c8c8c8; }
  .strip .slot img { width:20px; height:20px; object-fit:contain; }
  .strip span { color:var(--muted); font-size:12px; }
  main { padding:12px 16px 40px; }
  table { width:100%; border-collapse:collapse; background:#fff; border:1px solid var(--line); }
  th, td { border-bottom:1px solid var(--line); padding:8px; vertical-align:top; text-align:left; }
  th { background:#fafafa; font-weight:600; position:sticky; top:var(--head,120px); z-index:2; }
  td.role { width:170px; }
  td.role .name { font-weight:600; font-size:15px; }
  td.role .kind { color:var(--muted); font-size:12px; }
  td.role details { margin-top:6px; font-size:12px; color:var(--muted); }
  td.role code { font-size:11px; }
  td.current { width:260px; }
  .sizes { display:flex; flex-wrap:wrap; gap:8px; align-items:flex-end; }
  .sizes figure { margin:0; text-align:center; }
  .sizes img { display:block; image-rendering:auto; }
  .sizes figcaption { font-size:11px; color:var(--muted); }
  .compare { display:flex; gap:10px; margin-top:8px; align-items:center; }
  .compare .box { width:56px; height:56px; display:flex; align-items:center; justify-content:center; border:1px solid var(--line); border-radius:4px; }
  .compare .box img { width:48px; height:48px; object-fit:contain; }
  .compare .tb { width:28px; height:28px; display:flex; align-items:center; justify-content:center; background:#f0f0f0; border-radius:3px; }
  .compare .tb img { width:20px; height:20px; object-fit:contain; }
  .rowbar { display:flex; gap:6px; align-items:center; margin-bottom:6px; flex-wrap:wrap; }
  .rowbar input { width:150px; }
  .cands { display:flex; flex-wrap:wrap; gap:6px; }
  .cand { position:relative; width:76px; padding:4px; border:2px solid transparent; border-radius:6px; background:#fff; text-align:center; cursor:pointer; }
  .cand:hover { border-color:#c7e0f4; }
  .cand.picked { border-color:var(--pick); background:#eef7ee; }
  .cand img.big { width:48px; height:48px; display:block; margin:0 auto; }
  .cand .small { display:flex; justify-content:center; margin-top:3px; }
  .cand .small span { width:26px; height:26px; display:flex; align-items:center; justify-content:center; background:#f0f0f0; border-radius:3px; }
  .cand .small img { width:20px; height:20px; }
  .cand .label { font-size:10px; color:var(--muted); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; margin-top:2px; }
  .cand a { position:absolute; top:1px; right:3px; font-size:11px; text-decoration:none; color:var(--accent); }
  .status { color:var(--muted); font-size:12px; }
  .status.error { color:#a4262c; }
  footer { color:var(--muted); font-size:12px; margin-top:14px; }
</style>
</head>
<body>
<header id="head">
  <h1>Prompt Flipper - image inventory</h1>
  <div class="controls">
    <label>icons8 style <select id="style"></select></label>
    <label>Results per role <select id="amount"><option>12</option><option selected>24</option><option>48</option></select></label>
    <button class="primary" id="export">Export picks (JSON)</button>
    <button id="copy">Copy picks as table</button>
    <button id="clear">Clear picks</button>
    <span class="status" id="summary"></span>
  </div>
  <div class="strip">
    <span>Current</span><div class="row" id="stripCurrent"></div>
    <span>Picked</span><div class="row" id="stripPicked"></div>
  </div>
</header>
<main>
  <table>
    <thead><tr><th>Role</th><th>Current (every shipped size, then 48 px and 20 px toolbar view)</th><th>icons8 candidates - click to pick, "open" shows the icon on icons8.com</th></tr></thead>
    <tbody id="rows"></tbody>
  </table>
  <footer>
    Generated from __PROJECT_LABEL__ by app/Build-ImageInventoryPage.ps1. Candidates load live from the
    public icons8 search API; picks are stored in this browser only until exported.
  </footer>
</main>
<script id="inventory" type="application/json">__INVENTORY_JSON__</script>
<script>
(function () {
  'use strict';
  var STYLES = [
    ['fluency-systems-regular', 'Fluent Systems Regular (outline)'],
    ['fluency-systems-filled', 'Fluent Systems Filled'],
    ['ios', 'iOS outline'], ['ios-filled', 'iOS filled'], ['ios-glyphs', 'iOS glyphs'],
    ['material-outlined', 'Material outlined'], ['material-rounded', 'Material rounded'], ['material', 'Material filled'],
    ['win10', 'Windows 10'], ['metro', 'Windows Metro'], ['office', 'Office'],
    ['fluent', 'Fluency (color)'], ['color', 'Color'], ['color-glass', 'Color glass'],
    ['dusk', 'Dusk'], ['plasticine', 'Plasticine'], ['cotton', 'Cotton'], ['arcade', 'Arcade'], ['bubbles', 'Bubbles']
  ];
  var STORE = 'pf-image-inventory-v1';
  var data = JSON.parse(document.getElementById('inventory').textContent);
  var state = load();
  var styleSel = document.getElementById('style');
  var amountSel = document.getElementById('amount');

  function load() {
    try { var s = JSON.parse(localStorage.getItem(STORE)); if (s && s.picks) return s; } catch (e) {}
    return { style: STYLES[0][0], picks: {}, terms: {}, rowStyles: {} };
  }
  function save() { try { localStorage.setItem(STORE, JSON.stringify(state)); } catch (e) {} }
  function el(tag, attrs, kids) {
    var n = document.createElement(tag);
    if (attrs) Object.keys(attrs).forEach(function (k) {
      if (k === 'text') n.textContent = attrs[k]; else if (k === 'cls') n.className = attrs[k]; else n.setAttribute(k, attrs[k]);
    });
    (kids || []).forEach(function (c) { if (c) n.appendChild(c); });
    return n;
  }
  function iconUrl(id, px) { return 'https://img.icons8.com/?size=' + px + '&id=' + encodeURIComponent(id) + '&format=png'; }
  function pageUrl(p) { return 'https://icons8.com/icon/' + encodeURIComponent(p.id) + '/' + encodeURIComponent(p.commonName || p.name); }
  function largest(g) { return g.images[g.images.length - 1]; }
  function nearest(g, px) {
    var best = g.images[0];
    g.images.forEach(function (i) { if (Math.abs((i.Size || 0) - px) < Math.abs((best.Size || 0) - px)) best = i; });
    return best;
  }

  data.forEach(function (g) { g.images = g.Images; });

  STYLES.forEach(function (s) { styleSel.appendChild(el('option', { value: s[0], text: s[1] })); });
  styleSel.value = state.style;
  styleSel.addEventListener('change', function () { state.style = styleSel.value; save(); rows.forEach(function (r) { r.search(); }); });
  amountSel.addEventListener('change', function () { rows.forEach(function (r) { r.search(); }); });

  var rows = data.map(buildRow);
  document.documentElement.style.setProperty('--head', document.getElementById('head').offsetHeight + 'px');
  renderStrip();

  function buildRow(g) {
    var tbody = document.getElementById('rows');
    var names = el('details', null, [el('summary', { text: g.images.length + ' shipped file(s)' })]);
    g.images.forEach(function (i) { names.appendChild(el('div', null, [el('code', { text: i.LogicalName })])); });
    var role = el('td', { cls: 'role' }, [el('div', { cls: 'name', text: g.Label }), el('div', { cls: 'kind', text: g.Kind + ' - ' + g.Key }), names]);

    var sizes = el('div', { cls: 'sizes' });
    g.images.forEach(function (i) {
      var shown = Math.min(i.Width || 32, 64);
      var cap = i.Sizes && i.Sizes.length > 1 ? i.Sizes.join('/') + ' px' : (i.Width ? i.Width + ' px' : '?');
      // A role can ship one unsized master beside its per-size files.
      if (g.images.length > 1 && !/_\d+\.png$|^Toolbar\.\d+\./.test(i.LogicalName)) cap += ' master';
      sizes.appendChild(el('figure', null, [el('img', { src: i.Src, width: shown, height: shown, alt: i.LogicalName, title: i.LogicalName }), el('figcaption', { text: cap })]));
    });
    var big = largest(g), tb = nearest(g, 20);
    var compare = el('div', { cls: 'compare' }, [
      el('div', { cls: 'box' }, [el('img', { src: big.Src, alt: '' })]),
      el('div', { cls: 'tb' }, [el('img', { src: tb.Src, alt: '' })])
    ]);
    var current = el('td', { cls: 'current' }, [sizes, compare]);

    var term = el('input', { type: 'text', value: state.terms[g.Key] || g.SearchTerm, 'aria-label': 'search term' });
    var rowStyle = el('select', { 'aria-label': 'style for this row' });
    rowStyle.appendChild(el('option', { value: '', text: 'page style' }));
    STYLES.forEach(function (s) { rowStyle.appendChild(el('option', { value: s[0], text: s[1] })); });
    rowStyle.value = state.rowStyles[g.Key] || '';
    var go = el('button', { text: 'Search' });
    var status = el('span', { cls: 'status' });
    var cands = el('div', { cls: 'cands' });
    var cell = el('td', null, [el('div', { cls: 'rowbar' }, [term, rowStyle, go, status]), cands]);
    tbody.appendChild(el('tr', { id: 'row-' + g.Key }, [role, current, cell]));

    var seq = 0, loaded = false;
    function search() {
      var platform = rowStyle.value || state.style;
      var q = term.value.trim();
      state.terms[g.Key] = q; state.rowStyles[g.Key] = rowStyle.value; save();
      if (!q) return;
      var mine = ++seq;
      status.className = 'status'; status.textContent = 'loading...';
      var url = 'https://search.icons8.com/api/iconsets/v5/search?term=' + encodeURIComponent(q) +
        '&amount=' + amountSel.value + '&platform=' + encodeURIComponent(platform);
      fetch(url).then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); }).then(function (j) {
        if (mine !== seq) return;
        cands.textContent = '';
        var icons = (j && j.icons) || [];
        status.textContent = icons.length + ' of ' + ((j.parameters && j.parameters.countAll) || 0) + ' in ' + platform;
        icons.forEach(function (ic) { cands.appendChild(candidate(ic, platform)); });
        // Picks from another style stay visible so a row can compare across styles.
        (state.picks[g.Key] || []).forEach(function (p) {
          if (!icons.some(function (ic) { return ic.id === p.id; })) cands.insertBefore(candidate(p, p.platform), cands.firstChild);
        });
      }).catch(function (e) {
        if (mine !== seq) return;
        status.className = 'status error';
        status.textContent = 'icons8 search failed (' + e.message + '). Offline, or the anonymous API now needs a key.';
      });
    }
    function candidate(ic, platform) {
      var p = { id: ic.id, name: ic.name, commonName: ic.commonName, platform: ic.platform || platform };
      var picked = (state.picks[g.Key] || []).some(function (x) { return x.id === p.id; });
      var link = el('a', { href: pageUrl(p), target: '_blank', rel: 'noopener', title: 'open on icons8.com', text: 'open' });
      var box = el('div', { cls: 'cand' + (picked ? ' picked' : ''), title: p.name + ' (' + p.platform + ')' }, [
        el('img', { cls: 'big', src: iconUrl(p.id, 96), alt: p.name, loading: 'lazy' }),
        el('div', { cls: 'small' }, [el('span', null, [el('img', { src: iconUrl(p.id, 40), alt: '', loading: 'lazy' })])]),
        el('div', { cls: 'label', text: p.name }), link
      ]);
      link.addEventListener('click', function (e) { e.stopPropagation(); });
      box.addEventListener('click', function () {
        var list = state.picks[g.Key] || [];
        var at = -1;
        list.forEach(function (x, n) { if (x.id === p.id) at = n; });
        if (at >= 0) list.splice(at, 1); else list.push(p);
        if (list.length) state.picks[g.Key] = list; else delete state.picks[g.Key];
        box.classList.toggle('picked', at < 0);
        save(); renderStrip();
      });
      return box;
    }
    go.addEventListener('click', search);
    term.addEventListener('keydown', function (e) { if (e.key === 'Enter') search(); });
    rowStyle.addEventListener('change', search);
    var observer = new IntersectionObserver(function (entries) {
      if (!loaded && entries.some(function (x) { return x.isIntersecting; })) { loaded = true; observer.disconnect(); search(); }
    }, { rootMargin: '400px' });
    observer.observe(cell);
    return { search: function () { if (loaded) search(); } };
  }

  function renderStrip() {
    var cur = document.getElementById('stripCurrent'), pick = document.getElementById('stripPicked');
    cur.textContent = ''; pick.textContent = '';
    var count = 0;
    data.forEach(function (g) {
      var tb = nearest(g, 20);
      cur.appendChild(el('a', { cls: 'slot', href: '#row-' + g.Key, title: g.Label }, [el('img', { src: tb.Src, alt: g.Label })]));
      var p = (state.picks[g.Key] || [])[0];
      if (p) count++;
      pick.appendChild(el('a', { cls: 'slot' + (p ? '' : ' empty'), href: '#row-' + g.Key, title: p ? g.Label + ': ' + p.name : g.Label + ': no pick' },
        p ? [el('img', { src: iconUrl(p.id, 40), alt: p.name })] : []));
    });
    document.getElementById('summary').textContent = count + ' of ' + data.length + ' roles picked';
  }

  function picksExport() {
    return data.map(function (g) {
      return { key: g.Key, role: g.Label, kind: g.Kind, current: g.images.map(function (i) { return i.LogicalName; }),
        picks: (state.picks[g.Key] || []).map(function (p) {
          return { id: p.id, name: p.name, commonName: p.commonName, platform: p.platform, page: pageUrl(p), png: iconUrl(p.id, 256) };
        }) };
    });
  }
  document.getElementById('export').addEventListener('click', function () {
    var blob = new Blob([JSON.stringify({ generated: new Date().toISOString(), roles: picksExport() }, null, 2)], { type: 'application/json' });
    var a = el('a', { href: URL.createObjectURL(blob), download: 'icon-picks.json' });
    document.body.appendChild(a); a.click(); a.remove();
  });
  document.getElementById('copy').addEventListener('click', function () {
    var lines = ['| Role | Key | Pick | Style | icons8 page |', '|---|---|---|---|---|'];
    picksExport().forEach(function (r) {
      if (!r.picks.length) lines.push('| ' + r.role + ' | ' + r.key + ' | none | | |');
      r.picks.forEach(function (p) { lines.push('| ' + r.role + ' | ' + r.key + ' | ' + p.name + ' | ' + p.platform + ' | ' + p.page + ' |'); });
    });
    navigator.clipboard.writeText(lines.join('\n')).then(function () { document.getElementById('summary').textContent = 'Table copied'; });
  });
  document.getElementById('clear').addEventListener('click', function () {
    if (!confirm('Clear every pick?')) return;
    state.picks = {}; save(); document.querySelectorAll('.cand.picked').forEach(function (n) { n.classList.remove('picked'); }); renderStrip();
  });
})();
</script>
</body>
</html>
'@

if ($MyInvocation.InvocationName -ne '.') {
  $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
  if (-not $ProjectFile) { $ProjectFile = Join-Path $here 'PromptFlipper.csproj' }
  if (-not $ArtworkFile) { $ArtworkFile = Join-Path $here 'ToolbarArtwork.cs' }
  if (-not $OutputPath) {
    $OutputPath = Join-Path (Join-Path (Join-Path (Split-Path -Parent $here) 'artifacts') 'IconPack') 'image-inventory.html'
  }
  $outputDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
  if (-not (Test-Path -LiteralPath $outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory | Out-Null }

  $inventory = @(Get-ImageInventory -ProjectFile $ProjectFile -ArtworkFile $ArtworkFile -OutputDirectory $outputDirectory)
  if ($inventory.Count -eq 0) { throw "No EmbeddedResource images found in $ProjectFile" }
  $html = ConvertTo-InventoryHtml -Inventory $inventory -ProjectLabel ([IO.Path]::GetFileName($ProjectFile))
  [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $html, (New-Object Text.UTF8Encoding($false)))

  $imageCount = ($inventory | ForEach-Object { $_.Images.Count } | Measure-Object -Sum).Sum
  [pscustomobject]@{ OutputPath = [IO.Path]::GetFullPath($OutputPath); Roles = $inventory.Count; Images = $imageCount }
}
