# Builds the Microsoft Store package: dist\DeskToggle_<version>_x64.msix
#
#   .\scripts\pack-msix.ps1                       # local identity, for testing
#   .\scripts\pack-msix.ps1 -IdentityName "12345Publisher.DeskToggle" `
#       -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" -PublisherDisplayName "bhpdev1"
#
# The last three values come from Partner Center (Product identity). The package is left
# unsigned: the Store signs it on submission. Installing it locally needs either Developer Mode
# (Add-AppxPackage -Register on the layout folder) or a test certificate.
param(
    [string]$IdentityName = "bhpdev1.DeskToggle",
    [string]$Publisher = "CN=bhpdev1",
    [string]$PublisherDisplayName = "bhpdev1",
    [string]$Version
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root    = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\DesktopIconsToggle\DesktopIconsToggle.csproj"
$work    = Join-Path $root "dist\msix"
$layout  = Join-Path $work "layout"
$assets  = Join-Path $layout "Assets"

if (-not $Version) {
    $v = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $Version = "$v.0"  # the Store requires the fourth part to be 0
}

$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" |
    Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1
if (-not $kit) { throw "Windows SDK (makeappx.exe) not found." }
$makeappx = $kit.FullName
$makepri  = Join-Path $kit.DirectoryName "makepri.exe"

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $assets | Out-Null

# 1. NativeAOT exe. The ILCompiler locates the MSVC linker through vswhere.exe on PATH.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;" + $env:PATH
dotnet publish $project -c Release -r win-x64 -o (Join-Path $work "publish") | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $work "publish\DesktopIconsToggle.exe") $layout

# 2. Manifest
$manifest = (Get-Content (Join-Path $root "packaging\AppxManifest.xml") -Raw -Encoding UTF8).
    Replace('$IdentityName$', $IdentityName).Replace('$Publisher$', $Publisher).
    Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape($PublisherDisplayName)).
    Replace('$Version$', $Version)
[IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object Text.UTF8Encoding $false))

# 3. Visual assets from the brand logo: scale variants for tiles, target sizes (plated and
#    unplated) for the taskbar, Start and Settings icons.
$logo = [System.Drawing.Image]::FromFile((Join-Path $root "assets\brand\logo-512.png"))
function Save-Asset([string]$name, [int]$w, [int]$h, [double]$logoRatio) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [int][Math]::Round([Math]::Min($w, $h) * $logoRatio)
    $g.DrawImage($logo, [int](($w - $s) / 2), [int](($h - $s) / 2), $s, $s)
    $g.Dispose()
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
try {
    foreach ($scale in 100, 125, 150, 200, 400) {
        $f = $scale / 100
        Save-Asset "Square44x44Logo.scale-$scale.png"   ([int](44 * $f))  ([int](44 * $f))  1
        Save-Asset "Square150x150Logo.scale-$scale.png" ([int](150 * $f)) ([int](150 * $f)) 0.66
        Save-Asset "Wide310x150Logo.scale-$scale.png"   ([int](310 * $f)) ([int](150 * $f)) 0.66
        Save-Asset "StoreLogo.scale-$scale.png"         ([int](50 * $f))  ([int](50 * $f))  1
    }
    foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
        Save-Asset "Square44x44Logo.targetsize-$size.png" $size $size 1
        Save-Asset "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $size 1
        Save-Asset "Square44x44Logo.targetsize-${size}_altform-lightunplated.png" $size $size 1
    }
} finally {
    $logo.Dispose()
}

# 4. resources.pri, so Windows picks the right variant per scale and size
Push-Location $work
try {
    & $makepri createconfig /cf priconfig.xml /dq fr-FR /pv 10.0.0 /o | Out-Null
    # Single package, no bundle: keep every variant in one resources.pri instead of the
    # per-scale split files meant for resource packages.
    $cfg = [xml](Get-Content priconfig.xml)
    $pkg = $cfg.SelectSingleNode("//packaging")
    if ($pkg) { [void]$pkg.ParentNode.RemoveChild($pkg) }
    $cfg.Save((Join-Path $work "priconfig.xml"))
    & $makepri new /pr $layout /cf priconfig.xml /of (Join-Path $layout "resources.pri") /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makepri failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}

# 5. Package
$msix = Join-Path $root "dist\DeskToggle_${Version}_x64.msix"
$out = & $makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { $out | Out-Host; throw "makeappx failed ($LASTEXITCODE)" }
Write-Host "OK -> $msix"
