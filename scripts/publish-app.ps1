# Empaqueta Violeta como una aplicación de Windows lista para distribuir.
#
#   .\scripts\publish-app.ps1                # ejecutable único, autocontenido (no requiere .NET)
#   .\scripts\publish-app.ps1 -Framework     # más ligero, requiere .NET 8 Desktop Runtime instalado
#
# El resultado queda en dist\Violeta\.
param(
    [switch]$Framework,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'app\Violeta.App.csproj'
$out  = Join-Path $root 'dist\Violeta'

# Asegura el icono antes de compilar (ApplicationIcon lo necesita).
if (-not (Test-Path (Join-Path $root 'app\Assets\violeta.ico'))) {
    & (Join-Path $PSScriptRoot 'make-icon.ps1')
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$common = @(
    'publish', $proj,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $out,
    '-p:Version=1.0.0'
)

if ($Framework) {
    Write-Output "Publicando Violeta (dependiente del runtime, $Runtime)…"
    dotnet @common --self-contained false
} else {
    Write-Output "Publicando Violeta (autocontenido, ejecutable único, $Runtime)…"
    dotnet @common --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true
}

if ($LASTEXITCODE -ne 0) { throw "La publicación falló (dotnet salió con $LASTEXITCODE)." }

Write-Output ""
Write-Output "✅ Listo. Aplicación en: $out"
Write-Output "   Ejecutable: $(Join-Path $out 'Violeta.exe')"
Write-Output "   Comparte la carpeta $out (o solo Violeta.exe si es autocontenido)."
