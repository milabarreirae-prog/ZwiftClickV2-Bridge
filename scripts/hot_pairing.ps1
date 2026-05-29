# =============================================================
#  ZwiftClickV2-Bridge - Hot Pairing Workflow
# =============================================================
#  
#  Este script implementa el "Hot Pairing" de BikeControl:
#  1. Abre Zwift oficial, conecta el Click V2 ~30s
#  2. Cierra Zwift completamente
#  3. Ejecuta el bridge INMEDIATAMENTE
#  
#  Hipotesis actual: Zwift oficial deja disponible un flujo de
#  auth challenge/respuesta mientras la sesion OAuth y el estado
#  temporal del dispositivo siguen vigentes.
# =============================================================

$ErrorActionPreference = "Stop"

Write-Host "=== ZwiftClickV2-Bridge - HOT PAIRING WORKFLOW ===" -ForegroundColor Cyan
Write-Host ""

# --- Paso 1: Conectar Click V2 en Zwift oficial ---
Write-Host "--------------------------------------------------" -ForegroundColor DarkGray
Write-Host "  PASO 1: Conectar Click V2 en Zwift oficial" -ForegroundColor Yellow
Write-Host "--------------------------------------------------" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Requisitos:" -ForegroundColor White
Write-Host "    * Zwift debe estar instalado y corriendo" -ForegroundColor Gray
Write-Host "    * El Click V2 debe estar encendido (LED parpadeando)" -ForegroundColor Gray
Write-Host ""
Write-Host "  Instrucciones:" -ForegroundColor White
Write-Host "    1. Ve a la pantalla de dispositivos en Zwift" -ForegroundColor Gray
Write-Host "    2. Selecciona 'Zwift Click' de la lista" -ForegroundColor Gray
Write-Host "    3. Espera a que el LED del Click este azul/verde fijo" -ForegroundColor Gray
Write-Host "    4. Espera al menos 30 segundos conectado" -ForegroundColor Gray
Write-Host ""
Read-Host "  Presiona ENTER cuando el Click este conectado en Zwift"

# --- Paso 2: Cerrar Zwift ---
Write-Host ""
Write-Host "--------------------------------------------------" -ForegroundColor DarkGray
Write-Host "  PASO 2: Cerrar Zwift completamente" -ForegroundColor Yellow
Write-Host "--------------------------------------------------" -ForegroundColor DarkGray
Write-Host ""

# Buscar proceso Zwift
$zwiftProcess = Get-Process -Name "ZwiftApp" -ErrorAction SilentlyContinue
if ($zwiftProcess) {
    Write-Host "  ZwiftApp.exe encontrado. Cerrando..." -ForegroundColor Yellow
    Stop-Process -Name "ZwiftApp" -Force
    Start-Sleep -Seconds 2
    Write-Host "  [OK] Zwift cerrado" -ForegroundColor Green
} else {
    Write-Host "  [!] ZwiftApp.exe no encontrado. Ya lo cerraste manualmente?" -ForegroundColor Yellow
}

# Verificar que no haya quedado ningun proceso
$remaining = Get-Process -Name "ZwiftApp" -ErrorAction SilentlyContinue
if ($remaining) {
    Write-Host "  [ERROR] ZwiftApp.exe sigue corriendo. Cierralo manualmente." -ForegroundColor Red
    Write-Host "     Abre Administrador de Tareas y mata el proceso." -ForegroundColor Gray
    Read-Host "  Presiona ENTER cuando este cerrado"
} else {
    Write-Host "  [OK] Ningun proceso Zwift corriendo" -ForegroundColor Green
}

# --- Paso 3: Ejecutar bridge INMEDIATAMENTE ---
Write-Host ""
Write-Host "--------------------------------------------------" -ForegroundColor DarkGray
Write-Host "  PASO 3: Ejecutando bridge (reaprovechar estado temporal)" -ForegroundColor Yellow
Write-Host "--------------------------------------------------" -ForegroundColor DarkGray
Write-Host ""

# Delay minimo para liberar el stack BLE de Windows
Write-Host "  Esperando 800ms para liberar stack BLE de Windows..." -ForegroundColor Gray
Start-Sleep -Milliseconds 800

# Buscar el ejecutable o usar dotnet run
$exePath = Join-Path $PSScriptRoot "..\src\bin\Debug\net8.0-windows10.0.19041.0\ZwiftClickV2-Bridge.exe"
$projectPath = Join-Path $PSScriptRoot "..\src"

if (Test-Path $exePath) {
    Write-Host "  Ejecutando: $exePath --hot-pair" -ForegroundColor Green
    Write-Host ""
    & $exePath --hot-pair
} elseif (Test-Path $projectPath) {
    Write-Host "  Ejecutando: dotnet run --project src -- --hot-pair" -ForegroundColor Green
    Write-Host ""
    Push-Location (Join-Path $PSScriptRoot "..")
    & dotnet run --project src -- --hot-pair
    Pop-Location
} else {
    Write-Host "  [ERROR] No se encontro el proyecto." -ForegroundColor Red
    Write-Host "     Directorio actual: $(Get-Location)" -ForegroundColor Gray
    Write-Host "     Asegurate de estar en la raiz del repo." -ForegroundColor Gray
    Read-Host "  Presiona ENTER para salir"
    exit 1
}

# --- Resultado ---
Write-Host ""
Write-Host "--------------------------------------------------" -ForegroundColor DarkGray
if ($LASTEXITCODE -eq 0) {
    Write-Host "  [OK] HOT PAIRING COMPLETADO CON EXITO" -ForegroundColor Green
    Write-Host "  El bridge esta corriendo y emulando teclas." -ForegroundColor White
    Write-Host "  Abre MyWoosh y prueba los botones del Click V2." -ForegroundColor White
} else {
    Write-Host "  [ERROR] HOT PAIRING FALLO (exit code: $LASTEXITCODE)" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Posibles causas:" -ForegroundColor Yellow
    Write-Host "    * El dispositivo no acepto la sesion segura." -ForegroundColor Gray
    Write-Host "      -> Repite el proceso desde el Paso 1." -ForegroundColor Gray
    Write-Host "    * Zwift no dejo listo el estado de auth challenge/respuesta." -ForegroundColor Gray
    Write-Host "      -> Verifica que Zwift estuviera logueado y el Click conectado ~30s." -ForegroundColor Gray
    Write-Host "    * Esperaste demasiado entre cerrar Zwift y ejecutar el bridge." -ForegroundColor Gray
    Write-Host "      -> Intenta reducir el tiempo de espera." -ForegroundColor Gray
    Write-Host "    * El Click V2 no estaba conectado correctamente en Zwift." -ForegroundColor Gray
    Write-Host "      -> Asegurate de que el LED este azul/verde fijo en Zwift." -ForegroundColor Gray
    Write-Host "    * El dispositivo necesita bateria." -ForegroundColor Gray
    Write-Host "      -> Verifica que el Click este cargado." -ForegroundColor Gray
}
Write-Host "--------------------------------------------------" -ForegroundColor DarkGray
Write-Host ""
Read-Host "Presiona ENTER para salir"