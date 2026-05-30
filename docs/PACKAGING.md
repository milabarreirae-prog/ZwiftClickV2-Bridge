# Empaquetar **Violeta** para Windows 11

Violeta (`app/`) es una app WPF (.NET 8) que envuelve el bridge con una interfaz amigable. Esta guía
explica cómo generar un ejecutable bonito, gratuito y listo para repartir.

## Requisitos

- Windows 10/11 (build 2004+) con Bluetooth LE.
- **.NET 8 SDK** (incluye el workload de escritorio/WPF).

## Ejecutar en desarrollo

```powershell
dotnet run --project app
```

## Generar el ejecutable distribuible

El script hace todo (incluido el icono) y deja el resultado en `dist\Violeta\`:

```powershell
# Autocontenido: un Violeta.exe que NO necesita .NET instalado (recomendado para repartir)
.\scripts\publish-app.ps1

# Más ligero: requiere "Microsoft .NET 8 Desktop Runtime" en el PC de destino
.\scripts\publish-app.ps1 -Framework
```

Por dentro ejecuta:

```powershell
dotnet publish app\Violeta.App.csproj -c Release -r win-x64 -o dist\Violeta `
    --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## Repartir

- **Opción simple:** comprime la carpeta `dist\Violeta\` en un `.zip` y compártela. La persona solo
  descomprime y abre `Violeta.exe`. (Con `--self-contained`, basta el propio `Violeta.exe`.)
- **Instalador (opcional):** puedes envolver `dist\Violeta\` con [Inno Setup](https://jrsoftware.org/isinfo.php)
  o generar un MSIX con la *Windows Application Packaging* si quieres un instalador con accesos directos.

## El icono

`scripts\make-icon.ps1` genera `app\Assets\violeta.ico` (emblema lila: rueda + flor) en varios
tamaños. El `.csproj` lo usa como `ApplicationIcon`, así que el `.exe` y la barra de tareas ya lo
muestran. Vuelve a ejecutarlo si cambias el diseño.

## Notas

- Violeta no incrusta credenciales ni tokens: inicia sesión con la cuenta de la persona usuaria solo
  en memoria. Mantén ese principio si modificas el empaquetado.
- El binario es **gratis y libre (MIT)**. No le pongas precio: esa es la promesa del proyecto.
