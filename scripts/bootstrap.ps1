<#
.SYNOPSIS
    Bootstrap de prerequisitos de desarrollo de Spikit.

.DESCRIPTION
    Detecta los prerequisitos de dev del proyecto (.NET 8 SDK, Visual Studio
    2022 Community/Build Tools con workload .NET desktop development, e Inno
    Setup 6) e instala los que falten via winget.

    Detalle de prerequisitos en docs/infra.md (sección "Setup local de
    desarrollo").

.PARAMETER WhatIf
    Solo detecta, no instala. Util para inspeccionar el estado de la maquina
    sin tocar nada.

.EXAMPLE
    .\scripts\bootstrap.ps1
    Detecta e instala lo que falte.

.EXAMPLE
    .\scripts\bootstrap.ps1 -WhatIf
    Solo reporta que falta, no instala nada.

.NOTES
    Despues de instalar el .NET SDK por primera vez hay que REABRIR la
    terminal para que tome el PATH actualizado. El script lo recuerda al
    final si instalo el SDK.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Test-WingetPackageInstalled {
    param([Parameter(Mandatory)][string]$Id)
    $output = winget list --id $Id --exact --accept-source-agreements 2>&1 | Out-String
    # winget list imprime el ID en una columna cuando esta instalado.
    return $output -match [regex]::Escape($Id)
}

function Install-WingetPackage {
    param(
        [Parameter(Mandatory)][string]$Id,
        [string[]]$ExtraArgs = @()
    )
    $args = @(
        'install',
        '--id', $Id,
        '--exact',
        '--silent',
        '--accept-source-agreements',
        '--accept-package-agreements'
    ) + $ExtraArgs

    Write-Host "    -> winget $($args -join ' ')" -ForegroundColor DarkGray
    $proc = Start-Process -FilePath 'winget' -ArgumentList $args -NoNewWindow -PassThru -Wait
    return $proc.ExitCode
}

# ---------------------------------------------------------------------------
# Catalogo de prerequisitos
# ---------------------------------------------------------------------------
# Cada item:
#   Name        : etiqueta humana
#   CheckIds    : array de winget IDs que satisfacen el requisito (OR logico)
#   InstallId   : winget ID a usar si hay que instalar
#   InstallArgs : flags extra para el install (ej. --override de VS)

$prereqs = @(
    @{
        Name        = '.NET 8 SDK'
        CheckIds    = @('Microsoft.DotNet.SDK.8')
        InstallId   = 'Microsoft.DotNet.SDK.8'
        InstallArgs = @()
    },
    @{
        # Acepta cualquiera de los dos; si no hay ninguno, instala Build Tools
        # (mas liviano y suficiente para `dotnet build` + WPF designer).
        Name        = 'Visual Studio 2022 (Community o Build Tools) con workload .NET desktop development'
        CheckIds    = @(
            'Microsoft.VisualStudio.2022.Community',
            'Microsoft.VisualStudio.2022.BuildTools'
        )
        InstallId   = 'Microsoft.VisualStudio.2022.BuildTools'
        InstallArgs = @(
            '--override',
            '--add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --quiet --wait --norestart'
        )
    },
    @{
        Name        = 'Inno Setup 6'
        CheckIds    = @('JRSoftware.InnoSetup')
        InstallId   = 'JRSoftware.InnoSetup'
        InstallArgs = @()
    }
)

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '== Spikit dev bootstrap ==' -ForegroundColor Cyan
Write-Host ''

# Validar que winget exista (Win10 1809+ / Win11 lo trae; servers viejos no).
try {
    $null = winget --version
} catch {
    Write-Host '[ERROR] winget no esta disponible en esta maquina.' -ForegroundColor Red
    Write-Host '        Instalar "App Installer" desde Microsoft Store y reintentar.' -ForegroundColor Red
    exit 1
}

$alreadyInstalled = @()
$justInstalled    = @()
$pending          = @()
$installedDotnet  = $false

foreach ($p in $prereqs) {
    Write-Host "[*] $($p.Name)" -ForegroundColor White

    $present = $false
    foreach ($id in $p.CheckIds) {
        if (Test-WingetPackageInstalled -Id $id) {
            $present = $true
            Write-Host "    OK ($id)" -ForegroundColor Green
            $alreadyInstalled += $p.Name
            break
        }
    }

    if ($present) { continue }

    Write-Host '    falta.' -ForegroundColor Yellow

    if ($WhatIfPreference) {
        Write-Host '    (WhatIf) Se instalaria con winget.' -ForegroundColor DarkYellow
        $pending += $p.Name
        continue
    }

    Write-Host "    Instalando $($p.InstallId)..." -ForegroundColor Yellow
    $exit = Install-WingetPackage -Id $p.InstallId -ExtraArgs $p.InstallArgs

    if ($exit -eq 0) {
        Write-Host '    Instalado.' -ForegroundColor Green
        $justInstalled += $p.Name
        if ($p.InstallId -eq 'Microsoft.DotNet.SDK.8') { $installedDotnet = $true }
    } else {
        Write-Host "    [ERROR] winget exit code $exit. Revisar manualmente." -ForegroundColor Red
        $pending += $p.Name
    }
}

# ---------------------------------------------------------------------------
# Resumen
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '-- Resumen --' -ForegroundColor Cyan

if ($alreadyInstalled.Count -gt 0) {
    Write-Host 'Ya instalado:' -ForegroundColor Green
    $alreadyInstalled | ForEach-Object { Write-Host "  - $_" }
}

if ($justInstalled.Count -gt 0) {
    Write-Host 'Recien instalado:' -ForegroundColor Yellow
    $justInstalled | ForEach-Object { Write-Host "  - $_" }
}

if ($pending.Count -gt 0) {
    Write-Host 'Pendiente (revisar manualmente):' -ForegroundColor Red
    $pending | ForEach-Object { Write-Host "  - $_" }
}

Write-Host ''

if ($installedDotnet) {
    Write-Host '!! Instalaste el .NET SDK ahora. REABRI la terminal antes de correr `dotnet build`.' -ForegroundColor Magenta
    Write-Host '   (winget no refresca el PATH de la sesion actual.)' -ForegroundColor Magenta
    Write-Host ''
}

if ($pending.Count -gt 0) { exit 1 } else { exit 0 }
