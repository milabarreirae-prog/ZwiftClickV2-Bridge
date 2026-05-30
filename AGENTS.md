# ZwiftClickV2-Bridge — Agent Instructions

Bridge open-source para usar un **Zwift Click V2** (2025) con otras apps indoor vía emulación de
teclado en Windows. Habla el protocolo ZAP: handshake ECDH → unlock server-backed (cuenta del
usuario) → sesión AES-256-CCM.
Contexto: [README.md](README.md) · [docs/protocol/](docs/protocol/README.md) · [CONTRIBUTING.md](CONTRIBUTING.md)

## Build & Test

```bash
dotnet build                                   # compila solución
dotnet test                                    # tests unitarios (xUnit, sin hardware)
dotnet run --project src -- --test-crypto      # self-test cripto (sin hardware)
dotnet run --project src -- --diagnose         # handshake BLE (requiere device)
dotnet run --project src -- --bridge           # unlock + teclado (requiere device + cuenta Zwift)
```

`tests/` son tests unitarios reales (`[Fact]`, sin `[Theory]`). No hay harnesses de hardware en el repo.

## Arquitectura

| Carpeta | Responsabilidad |
|---|---|
| `Auth/` | `ZwiftOAuthClient` (login cuenta del usuario), `DeviceUnlockClient` (POST d-lock), `UnlockCoordinator`, `DeviceAuthChallenge` (protobuf). Nunca embebe tokens. |
| `BLE/` | `BleDeviceManager` (scan/connect, enlace **por UUID**), writer y listener. |
| `Bridge/` | `ZwiftClickBridge` (orquestador único del flujo). `KeyboardEmulator` (`SendInput`). |
| `Crypto/` | `ZapCrypto` (HKDF + AES-256-CCM), `ZPEncryptionV2` (contadores + ECDH raw), `ZPEncryptionV1` (Play legacy), `EcPoint` (compresión 64B→33B). |
| `Protocol/` | `ZapCommands` (handshake `02 03`, `FF 04 00`), `ApplicationLayerParser`, `ZopSequencer`, `ZapWireOpcode`, mensajes. |
| `Logging/` | `StructuredLogger` → `logs/session_*.json`. |

## Cripto (confirmado — ver docs/protocol/)

- ECDH P-256, secreto = **X cruda** (`DeriveRawSecretAgreement`, NO `DeriveKeyMaterial`).
- HKDF-SHA256, `salt = devicePubKey[64] ‖ localPubKey[64]` (128B), `info` ⚠️ contestado →
  `HkdfInfoMode.Empty` por defecto (decompile sugiere vacío); `LegacyHandshakeData` opcional.
- 36B → key[0:32] + ivBase[32:36]. AES-256-CCM, tag 4B, AAD vacío.
- Nonce `ivBase[4] ‖ counter[4]` (counter LE). Wire `[counter:4B LE][ct][tag:4B]`.

## Flujo de unlock (confirmado)

`handshake "RideOn 02 03"` → `POST d-lock-service/device/authenticate` (Bearer del token del
usuario) → `204` → write `FF 04 00` en CH03 → sesión cifrada en CH02. Detalle en
[docs/protocol/unlock-flow.md](docs/protocol/unlock-flow.md).

### Bloqueo abierto

El origen de los **campos 2 (id) y 3 (firma 40B)** del request d-lock. Único punto a completar:
`ZwiftClickBridge.TryAssembleChallenge`. No fabricar un request inválido mientras tanto.

## Convenciones

- Comentarios y mensajes de consola en español. `_camelCase` privados; `PascalCase`; async `...Async`.
- Nullability on; nunca el supresor `!` salvo invariantes ya garantizadas.
- TFM `net8.0-windows10.0.19041.0` (WinRT BLE). NuGet: `Google.Protobuf`, `Portable.BouncyCastle`
  (BCL preferido para primitivas; BouncyCastle solo en `ZPEncryptionV1`).
- Errores BLE devuelven `null`/`false`; errores de usuario imprimen `❌ …`.

## Nunca incluir en el repo

El decompile `x.c`, binarios de Zwift, ni capturas crudas (`out/mitm/`, `phaseC-captures/`) — pueden
contener tokens OAuth o datos del dispositivo. Solo conocimiento de protocolo sanitizado.
