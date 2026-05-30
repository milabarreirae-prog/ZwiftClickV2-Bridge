# Construye el instalador de Windows de Violeta (Inno Setup) y, opcionalmente, lo firma.
#
#   .\scripts\build-installer.ps1                 # genera dist\Violeta-Setup-1.0.0.exe
#   .\scripts\build-installer.ps1 -Sign           # además lo firma (autofirmado)
#   .\scripts\build-installer.ps1 -Sign -PfxPath cert.pfx -Password ***   # firma con tu cert real
#
# Requiere Inno Setup 6 (gratis): https://jrsoftware.org/isinfo.php
#   winget install -e --id JRSoftware.InnoSetup
param(
    [switch]$Sign,
    [string]$PfxPath,
    [string]$Password
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# 1) Asegurar el ejecutable publicado.
if (-not (Test-Path (Join-Path $root 'dist\Violeta\Violeta.exe'))) {
    Write-Output 'No hay build publicado; ejecutando publish-app.ps1…'
    & (Join-Path $PSScriptRoot 'publish-app.ps1')
}

# 2) Asegurar los bitmaps del asistente.
if (-not (Test-Path (Join-Path $root 'installer\assets\wizard.bmp'))) {
    & (Join-Path $PSScriptRoot 'make-installer-assets.ps1')
}

# 3) Localizar el compilador de Inno Setup (ISCC.exe).
$iscc = $null
$cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
if ($cmd) { $iscc = $cmd.Source }
if (-not $iscc) {
    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}
if (-not $iscc) {
    Write-Output ''
    Write-Output '❌ No encontré Inno Setup (ISCC.exe).'
    Write-Output '   Instálalo (gratis) y vuelve a ejecutar este script:'
    Write-Output '     winget install -e --id JRSoftware.InnoSetup'
    Write-Output '   o descárgalo de https://jrsoftware.org/isdl.php'
    exit 2
}

# 4) Compilar el instalador.
Write-Output "Compilando instalador con: $iscc"
& $iscc (Join-Path $root 'installer\Violeta.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC falló (salió con $LASTEXITCODE)." }

$setup = Join-Path $root 'dist\Violeta-Setup-1.0.0.exe'
if (-not (Test-Path $setup)) { throw "No se generó el instalador esperado: $setup" }

# 5) Firmar (opcional).
if ($Sign) {
    Write-Output ''
    Write-Output 'Firmando el instalador…'
    $signArgs = @{ File = $setup }
    if ($PfxPath) { $signArgs.PfxPath = $PfxPath; if ($Password) { $signArgs.Password = $Password } }
    & (Join-Path $PSScriptRoot 'sign-app.ps1') @signArgs
}

Write-Output ''
Write-Output "✅ Instalador listo: $setup ($('{0:N1}' -f ((Get-Item $setup).Length/1MB)) MB)"
