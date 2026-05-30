# ZwiftClickV2-Bridge

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Status](https://img.shields.io/badge/Status-Research%2FAlpha-orange)]()

Bridge open-source para usar tu **Zwift Click V2** (modelo 2025) con otras apps de ciclismo indoor
en Windows, mediante emulación de teclado. Habla el **Zwift Accessory Protocol (ZAP)**: handshake
ECDH, unlock server-backed con **tu propia cuenta Zwift**, y sesión cifrada AES-256-CCM.

> **Interoperabilidad para uso personal de hardware que posees.** No evade pagos ni DRM de
> contenido; no redistribuye software de Zwift. Lee el [aviso ético/legal](#-aviso-éticolegal).

## 📊 Estado

| Componente | Estado |
|---|---|
| Conexión BLE (scan por advertising, enlace por UUID) | ✅ |
| Handshake ZAP V2 (`RideOn 02 03`) | ✅ |
| Criptografía (ECDH P-256 raw → HKDF 128B → AES-256-CCM) | ✅ (self-test pasa) |
| Login OAuth con la cuenta del usuario + POST d-lock | ✅ implementado |
| Emulación de teclado (`SendInput`) | ✅ |
| **Unlock de extremo a extremo** | 🔬 **bloqueado por 1 incógnita** (ver abajo) |

### La incógnita que falta

El request de unlock (`POST /api/d-lock-service/device/authenticate`) lleva un protobuf con tres
campos: `{1: pubkey del dispositivo, 2: id, 3: firma de 40B}`. El campo 1 sale del handshake BLE;
**el origen exacto de los campos 2 y 3 (que emite el propio dispositivo por BLE) todavía no está
resuelto.** Hasta cerrarlo, el bridge llega hasta el handshake e informa con precisión dónde queda
bloqueado, sin fabricar un request inválido. Detalle en
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

Las credenciales de **tu** cuenta Zwift se leen de variables de entorno (o se piden de forma
interactiva) y solo se usan en memoria para obtener un `access_token` — **nunca se embeben ni se
guardan**:

```bash
$env:ZWIFT_USERNAME = "tu-email@example.com"
$env:ZWIFT_PASSWORD = "tu-contraseña"
dotnet run --project src -- --bridge
```

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
