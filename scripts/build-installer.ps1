#requires -Version 5.1
<#
.SYNOPSIS
    Genera el instalador Spikit-Setup.exe localmente usando Velopack (vpk).

.DESCRIPTION
    Reproduce el pipeline de release.yml en la maquina del dev. Util para validar
    cambios antes de publicar y para el smoke en Windows Sandbox del AC E2E del
    ticket EP-8.3.

    El comando workflow es:
        1. dotnet publish -c Release -r win-x64 --self-contained
        2. vpk pack (toma la carpeta de publish y genera Releases/Spikit-win-Setup.exe)

    Output queda en .\Releases\ (ignorado via .gitignore - los binarios no se
    commitean al repo). El ultimo Spikit-win-Setup.exe generado es el que se prueba
    en Sandbox o se publica a GitHub Releases.

.PARAMETER Version
    SemVer del release. Default: lee Version del Spikit.csproj.

.PARAMETER SentryDsn
    DSN de Sentry para inyectar como AssemblyMetadata. Si esta vacio, Sentry queda
    desactivado en el binario producido. CI lo pasa desde GitHub Secrets.

.PARAMETER SkipPublish
    Saltea dotnet publish (asume que la carpeta publish/ ya esta poblada). Util
    cuando se esta iterando sobre el wrapper de Velopack sin recompilar.

.EXAMPLE
    .\scripts\build-installer.ps1
    Build local sin Sentry, version leida del csproj.

.EXAMPLE
    .\scripts\build-installer.ps1 -Version 0.2.0 -SentryDsn $env:SPIKIT_SENTRY_DSN
    Smoke pre-release con DSN inyectado.
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$SentryDsn = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csprojPath = Join-Path $repoRoot "src\Spikit\Spikit.csproj"
$publishDir = Join-Path $repoRoot "publish"
$releasesDir = Join-Path $repoRoot "Releases"
$iconPath = Join-Path $repoRoot "assets\icono\icono.ico"

if (-not $Version) {
    $csprojContent = Get-Content $csprojPath -Raw
    if ($csprojContent -match '<Version>(?<v>[0-9]+\.[0-9]+\.[0-9]+)</Version>') {
        $Version = $Matches.v
    } else {
        throw "No se pudo leer la <Version> de $csprojPath. Pasala explicita con -Version."
    }
}

Write-Host "== Spikit installer build ==" -ForegroundColor Cyan
Write-Host "Version  : $Version"
$dsnNote = if ([string]::IsNullOrEmpty($SentryDsn)) { '<vacio - Sentry desactivado>' } else { '<inyectado>' }
Write-Host "SentryDsn: $dsnNote"
Write-Host ""

# 1. Publish self-contained win-x64. Sin single-file porque Velopack maneja el bundle.
if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

    Write-Host "[1/3] dotnet publish ..." -ForegroundColor Yellow
    $publishArgs = @(
        "publish", $csprojPath,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-o", $publishDir,
        "/p:Version=$Version",
        "/p:AssemblyVersion=$Version.0",
        "/p:FileVersion=$Version.0"
    )
    if (-not [string]::IsNullOrEmpty($SentryDsn)) {
        $publishArgs += "/p:SentryDsn=$SentryDsn"
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallo (exit $LASTEXITCODE)" }
}

# 2. vpk pack - genera Releases/Spikit-win-Setup.exe + nupkg incremental + manifest.
Write-Host "`n[2/3] vpk pack ..." -ForegroundColor Yellow
$vpkPath = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
if (-not (Test-Path $vpkPath)) {
    throw "vpk no encontrado en $vpkPath. Instalar con: dotnet tool install -g vpk"
}

$vpkArgs = @(
    "pack",
    "--packId", "Spikit",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--packAuthors", "Ignacio Poletti",
    "--packTitle", "Spikit",
    "--icon", $iconPath,
    "--mainExe", "Spikit.exe",
    "--outputDir", $releasesDir,
    "--shortcuts", "Desktop,StartMenuRoot",
    "-y"
)

& $vpkPath @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack fallo (exit $LASTEXITCODE)" }

# 3. Resumen de outputs.
Write-Host "`n[3/3] Outputs en $releasesDir" -ForegroundColor Yellow
Get-ChildItem $releasesDir -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object Name, @{N='SizeMB';E={[Math]::Round($_.Length/1MB,1)}}, LastWriteTime |
    Format-Table -AutoSize

$setupExe = Join-Path $releasesDir "Spikit-win-Setup.exe"
if (Test-Path $setupExe) {
    Write-Host "OK     : $setupExe" -ForegroundColor Green
    Write-Host "Probalo en Windows Sandbox o en una VM limpia."
} else {
    Write-Warning "No se encontro Spikit-win-Setup.exe - revisar output de vpk."
}
