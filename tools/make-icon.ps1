# Builds multi-size src/DesktopIconsToggle/app.ico (16, 20, 24, 32, 40, 48, 64, 256 px)
# from assets/brand/logo-512.png (rendered from assets/brand/logo.svg).
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root    = Split-Path $PSScriptRoot -Parent
$srcPng  = Join-Path $root "assets\brand\logo-512.png"
if (-not (Test-Path $srcPng)) {
    & (Join-Path $root "scripts\render-brand.ps1") -Only "logo.svg"
}

$source = [System.Drawing.Bitmap]::FromFile((Resolve-Path -LiteralPath $srcPng).Path)
$sizes  = @(16, 20, 24, 32, 40, 48, 64, 256)
$frames = New-Object System.Collections.Generic.List[byte[]]

try {
    foreach ($sz in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g   = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($source, (New-Object System.Drawing.Rectangle(0, 0, $sz, $sz)))
        $g.Dispose()

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($ms.ToArray())
        $ms.Dispose()
        $bmp.Dispose()
    }
}
finally {
    $source.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w   = New-Object System.IO.BinaryWriter($out)

# ICONDIR header (6 bytes)
$w.Write([UInt16]0)
$w.Write([UInt16]1)
$w.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz    = $sizes[$i]
    $bytes = $frames[$i]
    $dim   = if ($sz -ge 256) { [Byte]0 } else { [Byte]$sz }
    $w.Write($dim)                   # bWidth
    $w.Write($dim)                   # bHeight
    $w.Write([Byte]0)                # bColorCount
    $w.Write([Byte]0)                # bReserved
    $w.Write([UInt16]1)              # wPlanes
    $w.Write([UInt16]32)             # wBitCount
    $w.Write([UInt32]$bytes.Length)  # dwBytesInRes
    $w.Write([UInt32]$offset)        # dwImageOffset
    $offset += $bytes.Length
}

foreach ($bytes in $frames) {
    $w.Write($bytes)
}
$w.Flush()

$icoPath = Join-Path (Resolve-Path -LiteralPath (Join-Path $root "src\DesktopIconsToggle")).Path "app.ico"
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
Write-Output ("app.ico written ({0} sizes, {1} bytes)" -f $sizes.Count, $out.Length)
