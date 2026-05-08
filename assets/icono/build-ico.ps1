# Genera assets/icono/icono.ico multi-resolución desde icono_rojo.png.
# Embebe cada size como PNG (formato soportado por Windows Vista+, lo que cubre nuestro
# target Win10/Win11). Re-correr cuando cambie el asset fuente.
#
# Uso:  pwsh -File assets/icono/build-ico.ps1
#
# Resoluciones según design-system.md §1.4 + recomendación Microsoft para app icons.

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root "icono_rojo.png"
$dest = Join-Path $root "icono.ico"
$sizes = @(16, 24, 32, 48, 64, 128, 256)

if (-not (Test-Path $source)) {
    throw "Source PNG no existe: $source"
}

Write-Host "Source : $source"
Write-Host "Dest   : $dest"
Write-Host "Sizes  : $($sizes -join ', ')"

# Render cada tamaño en memoria → PNG bytes con HighQualityBicubic + transparencia preservada.
$srcImage = [System.Drawing.Image]::FromFile($source)
try {
    $pngBuffers = @{}
    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bitmap)
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.DrawImage($srcImage, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
            $g.Dispose()

            $ms = New-Object System.IO.MemoryStream
            $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngBuffers[$size] = $ms.ToArray()
            $ms.Dispose()
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $srcImage.Dispose()
}

# Construir el ICO: ICONDIR (6 bytes) + N × ICONDIRENTRY (16 bytes) + N × PNG payload.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out

# ICONDIR
$writer.Write([uint16]0)              # reserved
$writer.Write([uint16]1)              # type 1 = ICO
$writer.Write([uint16]$sizes.Count)   # number of images

# ICONDIRENTRY × N — calculamos offsets ahora que conocemos cantidad.
$headerSize = 6 + (16 * $sizes.Count)
$offset = $headerSize
foreach ($size in $sizes) {
    $bytes = $pngBuffers[$size]
    $dimByte = if ($size -ge 256) { [byte]0 } else { [byte]$size }  # 0 = 256 (1-byte unsigned cap)
    $writer.Write([byte]$dimByte)     # width
    $writer.Write([byte]$dimByte)     # height
    $writer.Write([byte]0)            # color palette (0 = sin paleta indexada)
    $writer.Write([byte]0)            # reserved
    $writer.Write([uint16]1)          # color planes
    $writer.Write([uint16]32)         # bits per pixel
    $writer.Write([uint32]$bytes.Length) # bytes in image data
    $writer.Write([uint32]$offset)    # offset al payload
    $offset += $bytes.Length
}

# Payloads PNG.
foreach ($size in $sizes) {
    $writer.Write($pngBuffers[$size])
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($dest, $out.ToArray())
$writer.Dispose()
$out.Dispose()

$generated = Get-Item $dest
Write-Host "OK     : $($generated.FullName) ($([Math]::Round($generated.Length / 1KB, 1)) KB)"
