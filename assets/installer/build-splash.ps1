#requires -Version 5.1
<#
.SYNOPSIS
    Genera assets/installer/splash.png para Velopack (--splashImage).

.DESCRIPTION
    Splash visible 1-2 segundos durante la instalacion del .exe (Velopack lo
    superpone con su progress bar interno). Diseno: logo Spikit centrado sobre
    canvas dark #0A0A0A. Sin glow procedural (descartado por Nacho 2026-05-08:
    el glow rojo en un splash de install se sentia recargado). El logo PNG ya
    incluye su propio glow sutil; lo dejamos hablar solo.

    Re-correr cuando cambien los assets de marca (logo_rojo.png) o si se ajusta
    el design-system.

.PARAMETER OutputPath
    Override del path de salida. Default: assets/installer/splash.png.
#>

[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

# Script vive en assets/installer/, asi que necesitamos subir 3 niveles para llegar
# al root del repo.
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$logoPath = Join-Path $root "assets\logo\logo_rojo.png"
if (-not $OutputPath) {
    $OutputPath = Join-Path $root "assets\installer\splash.png"
}

if (-not (Test-Path $logoPath)) {
    throw "logo_rojo.png no existe en $logoPath"
}

$outputDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

# Dimensiones del splash. 640x400 cumple con el rango razonable para Velopack
# (no microscopico ni gigante en pantallas estandar) y mantiene aspecto ~16:10
# que se ve bien centrado en escritorios FullHD.
$width = 640
$height = 400

# Tokens del design-system (docs/design-system.md):
#   bg:   #0A0A0A (PillBg / canvas oscuro V1)
#   brand: #FF3B30 (state.error.fg, color del simbolo)
$bg = [System.Drawing.Color]::FromArgb(255, 10, 10, 10)

$bmp = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
try {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear($bg)

        # Logo Spikit centrado, alto ~260px (proporcion preservada). cy ligeramente
        # arriba del centro deja respiracion abajo para el progress bar de Velopack
        # que se renderiza superpuesto.
        $logo = [System.Drawing.Image]::FromFile($logoPath)
        try {
            $targetH = 260.0
            $ratio = $logo.Width / [double]$logo.Height
            $targetW = $targetH * $ratio
            $drawX = ($width - $targetW) / 2.0
            $drawY = ($height - $targetH) / 2.0 - 20
            $rect = New-Object System.Drawing.RectangleF([single]$drawX, [single]$drawY, [single]$targetW, [single]$targetH)
            $g.DrawImage($logo, $rect)
        }
        finally {
            $logo.Dispose()
        }
    }
    finally {
        $g.Dispose()
    }

    $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $bmp.Dispose()
}

$out = Get-Item $OutputPath
Write-Host "OK: $($out.FullName) ($([Math]::Round($out.Length / 1KB, 1)) KB) ${width}x${height}"
