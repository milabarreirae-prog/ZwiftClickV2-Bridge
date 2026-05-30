# ZwiftClickV2-Bridge

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Status](https://img.shields.io/badge/Status-Research%2FAlpha-orange)]()

Bridge open-source para usar tu **Zwift Click V2** (modelo 2025) con otras apps de ciclismo indoor
en Windows, mediante emulación de teclado. Habla el **Zwift Accessory Protocol (ZAP)**: handshake
ECDH, unlock server-backed con **tu propia cuenta Zwift**, y sesión cifrada AES-256-CCM.

> **Interoperabilidad para uso personal de hardware que posees.** No evade pagos ni DRM de
> contenido; no redistribuye software de Zwift. Lee el [aviso ético/legal](#-aviso-éticolegal).

## 🖥️ App de escritorio: **Violeta**

Además de la CLI, el repo incluye **Violeta** (`app/`): una **aplicación de Windows 11 con interfaz
amigable**, pensada para cualquier persona. Tiene una pantalla **«Cómo funciona»** que explica, paso a
paso y en lenguaje humano, qué hace el software por detrás, y una pantalla **«Conectar»** que ilumina
cada paso **en tiempo real** mientras desbloquea tu mando. Paleta violeta/lila, logo de una ciclista
con una flor, y un mensaje claro: **es gratis, libre y siempre lo será**. Hecha con cariño por una
mujer trans 🏳️‍⚧️.

```powershell
dotnet run --project app                 # ejecutar la interfaz gráfica
.\scripts\publish-app.ps1                 # empaquetar Violeta.exe autocontenido en dist\Violeta\
```

La app reutiliza exactamente la misma lógica probada del bridge (BLE, cripto, unlock, teclado): no
reimplementa el protocolo, solo le pone una cara bonita. Detalle de empaquetado en
[`docs/PACKAGING.md`](docs/PACKAGING.md).

## 📊 Estado

| Componente | Estado |
|---|---|
| Conexión BLE (scan por advertising, enlace por UUID) | ✅ |
| Handshake ZAP V2 (`RideOn 02 03`) | ✅ |
| Captura del reto + POST d-lock + `FF 04 00` | ✅ |
| Login con la cuenta del usuario (password / refresh) | ✅ |
| **Unlock de extremo a extremo (sin la app oficial)** | ✅ **validado en hardware** |
| Criptografía de sesión (AES-256-CCM) | ✅ self-test |
| Decodificación de botones post-unlock | ✅ auto-resuelta por bake-off (tag CCM como oráculo); a confirmar en hardware |

### Cómo funciona el unlock (resuelto)

El **dispositivo genera el reto y lo emite en claro por CH02** (`FF 03 00 ‖ protobuf 82B`). El bridge
**no construye ni firma nada**: captura el blob, le quita el header y **reenvía los 82 bytes verbatim**
a `POST /api/d-lock-service/device/authenticate` con el `Bearer` de **tu** cuenta → `204` → escribe
`FF 04 00`. La cripto solo hace falta para decodificar botones después del unlock. Detalle en
[`docs/protocol/unlock-flow.md`](docs/protocol/unlock-flow.md).

## 🚀 Uso

### Requisitos

- **Windows 10/11** (build 2004+) con Bluetooth LE.
- **.NET 8.0 SDK**.
- Una **cuenta Zwift** (la tuya) y un **Zwift Click V2** que poseas.

### Compilar

```bash
git clone https://github.com/<tu-usuario>/ZwiftClickV2-Bridge.git
cd ZwiftClickV2-Bridge
dotnet build
```

### Ejecutar

```bash
# Self-test de criptografía (no requiere hardware ni cuenta)
dotnet run --project src -- --test-crypto

# Solo diagnóstico del handshake BLE (sin red, sin teclado)
dotnet run --project src -- --diagnose

# Unlock completo + emulación de teclado
dotnet run --project src -- --bridge
```

El token de **tu** cuenta Zwift se resuelve de variables de entorno (o, si faltan, se piden las
credenciales de forma interactiva) y solo se usa en memoria — **nunca se embebe ni se guarda**:

```bash
# Opción A: usuario + contraseña (password grant, client_id Zwift_Mobile_Link)
$env:ZWIFT_USERNAME = "tu-email@example.com"; $env:ZWIFT_PASSWORD = "tu-contraseña"
# Opción B: refresh_token (sin contraseña; útil con 2FA, client_id Game_Launcher)
$env:ZWIFT_REFRESH_TOKEN = "<tu refresh_token>"
# Opción C: un access_token ya en mano
$env:ZWIFT_ACCESS_TOKEN = "<tu access_token>"

dotnet run --project src -- --bridge
```

Despierta el Click pulsando un botón cuando aparezca el escaneo. Login: ver [docs/protocol/zwift-login.md](docs/protocol/zwift-login.md).

Flags: `--no-keyboard` (no emular teclas), `--legacy-hkdf-info` (probar `info="handshake data"`).

Mapeo de botones por defecto: **izquierda → `←`**, **derecha → `→`**.

## 📚 Documentación

| Documento | Descripción |
|---|---|
| [docs/protocol/](docs/protocol/README.md) | Referencia de protocolo graduada por evidencia (empieza aquí) |
| [docs/protocol/unlock-flow.md](docs/protocol/unlock-flow.md) | El flujo de unlock server-backed confirmado |
| [docs/REVERSE_ENGINEERING_V2.md](docs/REVERSE_ENGINEERING_V2.md) | Resumen de ingeniería inversa del Click V2 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Cómo contribuir |
| [docs/REVERSE_ENGINEERING.md](docs/REVERSE_ENGINEERING.md) | Histórico (Zwift Play V1) |

## 🛠️ Estructura

```
src/
├── Auth/      # OAuth (cuenta del usuario) + cliente d-lock + coordinador de unlock
├── BLE/       # Stack BLE (Windows.Devices.Bluetooth)
├── Bridge/    # ZwiftClickBridge (orquestador) + KeyboardEmulator
├── Crypto/    # ZapCrypto (HKDF + AES-256-CCM), ZPEncryptionV2, compresión EC
├── Protocol/  # Handshake, opcodes, framing ZOP, comandos
└── Logging/   # Logger JSON estructurado
tests/         # Tests unitarios (xUnit) — incluye la cadena de unlock con HTTP simulado
docs/          # Documentación de protocolo
```

## ⚖️ Aviso ético/legal

- Este proyecto es de **interoperabilidad para uso personal** de hardware Zwift Click V2 que el
  usuario **posee o está autorizado a usar**. No facilita el uso de hardware ajeno.
- El unlock requiere el token de **la propia cuenta Zwift del usuario** (login OAuth). La herramienta
  **nunca** embebe un token ni credenciales: así solo desbloqueas dispositivos asociados a tu cuenta.
- **No** evade pagos ni el DRM de contenido de Zwift; **no** redistribuye binarios de Zwift ni el
  decompile de su app. Esos artefactos quedan deliberadamente fuera de este repositorio.
- Úsalo de forma responsable y conforme a las leyes y a los términos de servicio que te apliquen.

## 🙏 Agradecimientos

- **Makinolo** — [Protocolo Zwift Play V1](https://www.makinolo.com/blog/2023/10/08/connecting-to-zwift-play-controllers/)
- **ajchellew/zwiftplay** — [Implementación de referencia](https://github.com/ajchellew/zwiftplay)
- **QDomyos-Zwift** — [Comunidad e inspiración](https://github.com/cagnulein/qdomyos-zwift)
- El trabajo de ingeniería inversa que resolvió el protocolo ZAP V2 (ver [docs/protocol/](docs/protocol/README.md)).

## 📄 Licencia

[MIT](LICENSE).

---

*Proyecto no afiliado a Zwift, Inc. Zwift, Zwift Click y Zwift Play son marcas de sus respectivos
dueños. Sin garantía de ningún tipo.*
