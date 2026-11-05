<#
.SYNOPSIS
Packs the published Prompt Flipper executable into one signed per-user MSIX and reports the
package identity a tester needs to confirm the copy on the target machine.

.DESCRIPTION
ADR-002 selected MSIX as the release package. This script produces that same artifact one step
before a Store submission, so a corporate laptop test exercises the package that ships rather
than a hand-copied executable.

It does not build the application. The publish is owned by Publish-PromptFlipper.ps1 and this
script calls it, or takes an existing publish folder with -SkipPublish.

Four steps, each of which the script performs itself so that a package produced on one machine
matches a package produced on another:

1. Resolve makeappx.exe and signtool.exe. A Windows SDK installation is used when one is
   present. Otherwise the Microsoft.Windows.SDK.BuildTools package is downloaded once into a
   per-user cache under LOCALAPPDATA and reused by every later run.
2. Stage the payload: the published executable, the manifest from Package\AppxManifest.xml
   with its three placeholders substituted, and the three logo assets rendered from
   PromptFlipper.ico so that no image is kept in the repository as a second copy of the icon.
3. Pack the staged folder into a .msix.
4. Sign it with a code signing certificate. Without -CertificateThumbprint a development
   certificate whose subject equals -Publisher is created in the current user's store and
   reused on later runs. The matching public certificate is written beside the package.

A development certificate makes the package installable only where that certificate is
trusted. It is not a distribution route. See ..\docs\release-packaging.md.

.PARAMETER OutputPath
Directory the .msix and the .cer are written to. Defaults to a "package-out" folder beside this
script, which is ignored by git. The name differs from the "Package" source folder by more than
case, because git on Windows ignores case and one pattern would otherwise hide the manifest.

.PARAMETER PublishPath
Directory holding the published executable. Defaults to the "publish" folder beside this
script, which is where Publish-PromptFlipper.ps1 writes by default.

.PARAMETER SkipPublish
Packs the executable already in -PublishPath instead of publishing first. Use only to
re-package an executable whose hash has already been recorded.

.PARAMETER PackageVersion
Four-part package version written into the manifest. Defaults to 1.0.0.0. The Store requires
the last part to be 0.

.PARAMETER Publisher
Certificate subject written into the manifest Identity. It must match the subject of the
signing certificate exactly, which is why the script writes both from this one value.

.PARAMETER PublisherDisplayName
The publisher name a tester sees in the install dialog.

.PARAMETER CertificateThumbprint
Thumbprint of an existing code signing certificate to sign with. Without it the script finds
or creates a development certificate in Cert:\CurrentUser\My.

.PARAMETER IdentityName
Package identity name written into the manifest. Defaults to PromptFlipper for a test
package. A Store upload takes Package/Identity/Name from the Partner Center Product identity
page.

.PARAMETER StoreUpload
Builds the Store upload package: the manifest carries the Partner Center identity given in
-IdentityName, -Publisher and -PublisherDisplayName, and the package is left unsigned because
Microsoft signs it after certification (ADR-002). No certificate is created or exported.

.EXAMPLE
.\Build-PromptFlipperPackage.ps1

Publishes, packs and signs with a development certificate, and prints the package path, size
and SHA-256.

.EXAMPLE
.\Build-PromptFlipperPackage.ps1 -SkipPublish -PackageVersion '1.0.1.0'

Re-packages the executable already published, under a higher package version.

.EXAMPLE
.\Build-PromptFlipperPackage.ps1 -StoreUpload -IdentityName '12345Publisher.PromptFlipper' -Publisher 'CN=00000000-0000-0000-0000-000000000000' -PublisherDisplayName 'Publisher' -PackageVersion '1.0.3.0'

Builds the unsigned Store upload package. The three identity values shown are labelled
examples; copy the real ones from Partner Center Product identity.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath,

    [ValidateNotNullOrEmpty()]
    [string]$PublishPath,

    [switch]$SkipPublish,

    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$PackageVersion = '1.0.0.0',

    [ValidateNotNullOrEmpty()]
    [string]$Publisher = 'CN=Prompt Flipper Development',

    [ValidateNotNullOrEmpty()]
    [string]$PublisherDisplayName = 'Prompt Flipper Development',

    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CertificateThumbprint,

    [ValidatePattern('^[A-Za-z0-9.\-]{3,50}$')]
    [string]$IdentityName = 'PromptFlipper',

    [switch]$StoreUpload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The build tools package that carries makeappx.exe and signtool.exe when no Windows SDK is
# installed. Pinned so that two machines resolve the same tool versions.
$script:BuildToolsPackageId = 'Microsoft.Windows.SDK.BuildTools'
$script:BuildToolsVersion = '10.0.28000.2705'

function Invoke-PackagingTool {
    <#
    .SYNOPSIS
    Runs a native packaging tool and turns a non-zero exit code into a terminating error.

    .DESCRIPTION
    makeappx and signtool write progress to stderr on success. Merging the streams keeps the
    harness from reading that as a failure, and the exit code stays the only success test.
    [see-SoR: ps-native-stderr]
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$Activity
    )

    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Activity failed with exit code $LASTEXITCODE.$([Environment]::NewLine)$($output -join [Environment]::NewLine)"
    }

    $output
}

function Resolve-PackagingTool {
    <#
    .SYNOPSIS
    Returns the full path of an x64 Windows SDK packaging tool, installing the build tools
    package on first use when no SDK is present.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('makeappx.exe', 'signtool.exe')]
        [string]$Name
    )

    $kitRoot = Join-Path -Path ${env:ProgramFiles(x86)} -ChildPath 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitRoot) {
        $installed = Get-ChildItem -LiteralPath $kitRoot -Recurse -Filter $Name -File -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -like '*\x64' } |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1
        if ($null -ne $installed) {
            return $installed.FullName
        }
    }

    $cacheRoot = Join-Path -Path $env:LOCALAPPDATA -ChildPath "PromptFlipper\sdk-buildtools\$script:BuildToolsVersion"
    $cached = Get-ChildItem -LiteralPath $cacheRoot -Recurse -Filter $Name -File -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like '*\x64' } |
        Select-Object -First 1
    if ($null -ne $cached) {
        return $cached.FullName
    }

    Write-Verbose "Downloading $script:BuildToolsPackageId $script:BuildToolsVersion into $cacheRoot."
    $packageId = $script:BuildToolsPackageId.ToLowerInvariant()
    $uri = "https://api.nuget.org/v3-flatcontainer/$packageId/$script:BuildToolsVersion/$packageId.$script:BuildToolsVersion.nupkg"
    $archive = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath "$packageId.$script:BuildToolsVersion.zip"

    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $uri -OutFile $archive -UseBasicParsing
    [System.IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
    Add-Type -AssemblyName 'System.IO.Compression.FileSystem'
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $cacheRoot)
    Remove-Item -LiteralPath $archive -Force

    $extracted = Get-ChildItem -LiteralPath $cacheRoot -Recurse -Filter $Name -File |
        Where-Object { $_.DirectoryName -like '*\x64' } |
        Select-Object -First 1
    if ($null -eq $extracted) {
        throw "$Name was not found in $script:BuildToolsPackageId $script:BuildToolsVersion."
    }

    $extracted.FullName
}

function Get-IconFrameImage {
    <#
    .SYNOPSIS
    Returns the frame of an .ico file that is the closest fit at or above the requested edge
    length, as a disposable image.

    .DESCRIPTION
    Every frame of PromptFlipper.ico is PNG compressed, and System.Drawing.Icon cannot decode
    that form: ToBitmap throws "Requested range extends past the end of the array". Reading the
    icon directory directly and handing the frame's own bytes to the image decoder is what
    works for both frame forms. The caller disposes the result.
    #>
    [CmdletBinding()]
    [OutputType([System.Drawing.Image])]
    param(
        [Parameter(Mandatory = $true)][string]$IconPath,
        [Parameter(Mandatory = $true)][int]$EdgeLength
    )

    $bytes = [System.IO.File]::ReadAllBytes($IconPath)
    $frameCount = [BitConverter]::ToUInt16($bytes, 4)
    if ($frameCount -lt 1) {
        throw "The icon holds no frames: $IconPath"
    }

    $frames = foreach ($index in 0..($frameCount - 1)) {
        $entry = 6 + ($index * 16)
        $width = $bytes[$entry]
        if ($width -eq 0) { $width = 256 }
        [pscustomobject]@{
            Width  = $width
            Length = [BitConverter]::ToUInt32($bytes, $entry + 8)
            Offset = [BitConverter]::ToUInt32($bytes, $entry + 12)
        }
    }

    $frame = $frames | Where-Object { $_.Width -ge $EdgeLength } | Sort-Object -Property Width | Select-Object -First 1
    if ($null -eq $frame) {
        $frame = $frames | Sort-Object -Property Width -Descending | Select-Object -First 1
    }

    $isPng = $bytes[$frame.Offset] -eq 0x89 -and $bytes[$frame.Offset + 1] -eq 0x50
    if ($isPng) {
        $stream = New-Object -TypeName 'System.IO.MemoryStream' -ArgumentList (, $bytes[$frame.Offset..($frame.Offset + $frame.Length - 1)])
        return [System.Drawing.Image]::FromStream($stream)
    }

    $icon = New-Object -TypeName 'System.Drawing.Icon' -ArgumentList $IconPath, $frame.Width, $frame.Width
    try {
        return $icon.ToBitmap()
    }
    finally {
        $icon.Dispose()
    }
}

function Export-PackageAsset {
    <#
    .SYNOPSIS
    Renders one square PNG logo of the requested edge length from the application icon.

    .DESCRIPTION
    The icon is the single source of the application's image. Rendering the manifest assets
    from it at pack time keeps the repository from holding a second copy that drifts.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$IconPath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][ValidateRange(16, 512)][int]$EdgeLength
    )

    Add-Type -AssemblyName 'System.Drawing'

    $source = $null
    $bitmap = $null
    $graphics = $null
    try {
        $source = Get-IconFrameImage -IconPath $IconPath -EdgeLength $EdgeLength
        $bitmap = New-Object -TypeName 'System.Drawing.Bitmap' -ArgumentList $EdgeLength, $EdgeLength
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($source, 0, 0, $EdgeLength, $EdgeLength)
        $bitmap.Save($DestinationPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        foreach ($disposable in @($graphics, $bitmap, $source)) {
            if ($null -ne $disposable) { $disposable.Dispose() }
        }
    }
}

function Get-SigningCertificate {
    <#
    .SYNOPSIS
    Returns the code signing certificate whose subject matches the manifest publisher, creating
    a development certificate when none exists and no thumbprint was given.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Subject,
        [string]$Thumbprint
    )

    if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
        $named = Get-ChildItem -Path 'Cert:\CurrentUser\My' |
            Where-Object { $_.Thumbprint -eq $Thumbprint } |
            Select-Object -First 1
        if ($null -eq $named) {
            throw "No certificate with thumbprint $Thumbprint was found in Cert:\CurrentUser\My."
        }
        if ($named.Subject -ne $Subject) {
            throw "Certificate $Thumbprint has subject '$($named.Subject)' but the manifest publisher is '$Subject'. A package whose publisher does not match its signature is rejected at install time."
        }
        return $named
    }

    $existing = Get-ChildItem -Path 'Cert:\CurrentUser\My' -CodeSigningCert |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object -Property NotAfter -Descending |
        Select-Object -First 1
    if ($null -ne $existing) {
        return $existing
    }

    Write-Verbose "Creating a development code signing certificate for '$Subject'."
    New-SelfSignedCertificate -Type 'CodeSigningCert' -Subject $Subject `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 2048 `
        -FriendlyName 'Prompt Flipper development signing' `
        -NotAfter (Get-Date).AddYears(1) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}Subject Type:End Entity')
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifestTemplate = Join-Path -Path $scriptRoot -ChildPath 'Package\AppxManifest.xml'
$publishScript = Join-Path -Path $scriptRoot -ChildPath 'Publish-PromptFlipper.ps1'
$iconPath = Join-Path -Path $scriptRoot -ChildPath 'PromptFlipper.ico'

foreach ($required in @($manifestTemplate, $publishScript, $iconPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "A file this script depends on was not found: $required"
    }
}

if ([string]::IsNullOrWhiteSpace($PublishPath)) {
    $PublishPath = Join-Path -Path $scriptRoot -ChildPath 'publish'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path -Path $scriptRoot -ChildPath 'package-out'
}

$fullPublishPath = [System.IO.Path]::GetFullPath($PublishPath)
$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$packagePath = Join-Path -Path $fullOutputPath -ChildPath "PromptFlipper_$PackageVersion`_x64.msix"

if ($StoreUpload) {
    # A Store upload carries the Partner Center identity and no local signature. The defaults
    # are test values, so leaving any of them in place would build a package Partner Center rejects.
    if ($IdentityName -eq 'PromptFlipper' -or $Publisher -eq 'CN=Prompt Flipper Development' -or $PublisherDisplayName -eq 'Prompt Flipper Development') {
        throw '-StoreUpload needs -IdentityName, -Publisher and -PublisherDisplayName copied from Partner Center Product identity.'
    }
    if ($PSBoundParameters.ContainsKey('CertificateThumbprint')) {
        throw '-StoreUpload builds an unsigned package; do not pass -CertificateThumbprint (ADR-002).'
    }
    $packagePath = Join-Path -Path $fullOutputPath -ChildPath "PromptFlipper_$PackageVersion`_x64_store.msix"
}

if (-not $PSCmdlet.ShouldProcess($packagePath, 'Pack Prompt Flipper as MSIX')) {
    return
}

if ($SkipPublish) {
    if (-not (Test-Path -LiteralPath $fullPublishPath -PathType Container)) {
        throw "-SkipPublish was given but the publish folder does not exist: $fullPublishPath"
    }
}
else {
    Write-Verbose "Publishing into $fullPublishPath."
}

$published = & $publishScript -OutputPath $fullPublishPath -InspectOnly:$SkipPublish
$executable = Get-Item -LiteralPath $published.Path

$makeAppx = Resolve-PackagingTool -Name 'makeappx.exe'
if ($StoreUpload) {
    $manifestPublisher = $Publisher
}
else {
    $signTool = Resolve-PackagingTool -Name 'signtool.exe'
    $certificate = Get-SigningCertificate -Subject $Publisher -Thumbprint $CertificateThumbprint
    $manifestPublisher = $certificate.Subject
}

$stagingPath = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ("PromptFlipperPackage_" + [guid]::NewGuid().ToString('n'))
try {
    $assetsPath = Join-Path -Path $stagingPath -ChildPath 'Assets'
    [System.IO.Directory]::CreateDirectory($assetsPath) | Out-Null

    Copy-Item -LiteralPath $executable.FullName -Destination (Join-Path -Path $stagingPath -ChildPath $executable.Name)

    # Edge lengths are the three the manifest names. Scale factor variants are a Store concern
    # and are not produced for a test package.
    $assets = @{
        'Square44x44Logo.png'   = 44
        'Square150x150Logo.png' = 150
        'StoreLogo.png'         = 50
    }
    foreach ($assetName in $assets.Keys) {
        Export-PackageAsset -IconPath $iconPath -DestinationPath (Join-Path -Path $assetsPath -ChildPath $assetName) -EdgeLength $assets[$assetName]
    }

    $manifest = Get-Content -LiteralPath $manifestTemplate -Raw
    $manifest = $manifest.Replace('{IDENTITY_NAME}', $IdentityName).Replace('{PUBLISHER}', $manifestPublisher).Replace('{PUBLISHER_DISPLAY_NAME}', $PublisherDisplayName).Replace('{VERSION}', $PackageVersion)
    Set-Content -LiteralPath (Join-Path -Path $stagingPath -ChildPath 'AppxManifest.xml') -Value $manifest -Encoding utf8

    [System.IO.Directory]::CreateDirectory($fullOutputPath) | Out-Null
    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force
    }

    Invoke-PackagingTool -FilePath $makeAppx -Activity 'makeappx pack' -ArgumentList @(
        'pack', '/d', $stagingPath, '/p', $packagePath, '/o'
    ) | Write-Verbose

    if (-not $StoreUpload) {
        Invoke-PackagingTool -FilePath $signTool -Activity 'signtool sign' -ArgumentList @(
            'sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint, $packagePath
        ) | Write-Verbose
    }
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}

# The public certificate is what a tester imports to trust the package. It carries no private
# key and is not a secret.
$certificatePath = $null
$certificateThumbprintOut = $null
if (-not $StoreUpload) {
    $certificatePath = Join-Path -Path $fullOutputPath -ChildPath 'PromptFlipper.cer'
    Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT | Out-Null
    $certificateThumbprintOut = $certificate.Thumbprint
}

$package = Get-Item -LiteralPath $packagePath
$packageHash = Get-FileHash -LiteralPath $packagePath -Algorithm SHA256

[pscustomobject]@{
    Path                  = $package.FullName
    SizeMB                = [math]::Round($package.Length / 1MB, 1)
    Sha256                = $packageHash.Hash
    PackageVersion        = $PackageVersion
    IdentityName          = $IdentityName
    Publisher             = $manifestPublisher
    Signed                = (-not $StoreUpload)
    CertificateThumbprint = $certificateThumbprintOut
    CertificatePath       = $certificatePath
    BuiltUtc              = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')
}
