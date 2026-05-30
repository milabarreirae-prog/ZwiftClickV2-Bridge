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

## Firmar el ejecutable (SmartScreen)

```powershell
.\scripts\sign-app.ps1                                  # certificado AUTOFIRMADO (pruebas)
.\scripts\sign-app.ps1 -PfxPath cert.pfx -Password ***  # tu certificado real (.pfx)
.\scripts\sign-app.ps1 -File dist\Violeta-Setup-1.0.0.exe -PfxPath cert.pfx -Password ***
```

Usa `Set-AuthenticodeSignature` con sellado de tiempo, así que no necesitas `signtool`.

> ⚠️ **Sobre el aviso de «editor desconocido»:** un certificado **autofirmado** firma el binario
> (integridad + autoría declarada) pero **no** quita el aviso de SmartScreen, porque no está en la
> cadena de confianza de Windows. Para eliminarlo de verdad necesitas un **certificado de firma de
> código de una CA reconocida** (OV, o EV para confianza inmediata). Cuando lo tengas, pásalo con
> `-PfxPath` y el mismo script lo usa. La reputación de SmartScreen también mejora con el tiempo y las
> descargas.

## Instalador de Windows (Inno Setup)

Genera un `Setup.exe` con asistente en violeta, accesos directos y desinstalador.

```powershell
.\scripts\build-installer.ps1            # -> dist\Violeta-Setup-1.0.0.exe
.\scripts\build-installer.ps1 -Sign      # además firma el instalador (autofirmado)
.\scripts\build-installer.ps1 -Sign -PfxPath cert.pfx -Password ***   # firma con tu cert real
```

Necesita **Inno Setup 6** (gratis). Si no lo tienes:

```powershell
winget install -e --id JRSoftware.InnoSetup
```

- El script de instalador (`installer\Violeta.iss`) instala **por usuario** (sin pedir admin),
  crea acceso directo en el menú Inicio y, opcionalmente, en el escritorio, y ofrece abrir Violeta al
  terminar. Idiomas: español e inglés.
- Los bitmaps del asistente se generan con `scripts\make-installer-assets.ps1` (lila + emblema).

## Notas

- Violeta no incrusta credenciales ni tokens: inicia sesión con la cuenta de la persona usuaria solo
  en memoria. Mantén ese principio si modificas el empaquetado.
- El binario es **gratis y libre (MIT)**. No le pongas precio: esa es la promesa del proyecto.
