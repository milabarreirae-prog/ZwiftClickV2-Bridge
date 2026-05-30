# Ingeniería inversa — Zwift Click V2 (ZAP)

> **Estado: RESUELTO y VALIDADO EN HARDWARE.** La cadena de unlock completa se reprodujo sin la app
> oficial (2026-05-30). Este documento es el resumen; la referencia de protocolo graduada por
> evidencia vive en [`docs/protocol/`](protocol/README.md).

## TL;DR

El Click V2 no se desbloquea localmente. Su bloqueo es **DRM con respaldo de servidor**: la app
oficial valida la cuenta/dispositivo contra el servidor de Zwift (con el `access_token` de la cuenta
del usuario) y solo entonces confirma el unlock por BLE. La cadena completa:

```
1. BLE   handshake  "RideOn 02 03" + localPubKey[64]      (write CH03; el 58 02 en CH04 NO es fatal)
2. BLE   el DISPOSITIVO emite EN CLARO en CH02: FF 03 00 ‖ protobuf(82B)
         { 1: devicePubKey_comprimida(33B), 2: id, 3: firma(40B) }   ← lo genera el device
3. HTTP  POST /api/d-lock-service/device/authenticate      Authorization: Bearer <access_token>
         body = esos 82B VERBATIM (sin Content-Type)                 → 204 No Content
4. BLE   write "FF 04 00" → CH03                           (señal de unlock, solo tras el 204)
5.       sesión cifrada AES-256-CCM fluye en CH02
```

**Clave:** el bridge **no construye ni firma** los campos del reto — el dispositivo entrega el
protobuf completo en claro por CH02 y se reenvía verbatim. La cripto solo hace falta para los
botones/telemetría post-unlock. Detalle y evidencia: [`docs/protocol/unlock-flow.md`](protocol/unlock-flow.md).

## Correcciones respecto a versiones previas de este documento

Auditoría adversarial del decompile + hardware corrigió varios puntos que antes estaban mal:

| Tema | Antes (incorrecto) | Ahora (confirmado) |
|---|---|---|
| Estado del unlock | "punto muerto / DRM impenetrable" | **RESUELTO**: flujo server-backed conocido |
| Cipher | AES-128-**GCM** | **AES-256-CCM**, tag 4B, AAD vacío |
| Tamaño de clave | 16B | **32B** (output HKDF 36B → key[0:32] + ivBase[32:36]) |
| HKDF salt | 96B (`peer ‖ SHA256(peer)`) | **128B** `devicePubKey[64] ‖ localPubKey[64]` (device primero) |
| HKDF info | `"handshake data"` | ⚠️ **probablemente vacío** (sin resolver; configurable, ver abajo) |
| Secreto ECDH | SHA256(X) (`DeriveKeyMaterial`) | **X cruda** (`DeriveRawSecretAgreement`) |
| Prefijo handshake | `01 02` / `01 03` (V1) | **`02 03`** (V2) |
| `58 02` | "rechazo DRM en cada caso" | en gran parte **artefactos de framing HCI**; el rechazo real es ATT por falta de auth de servidor |
| Opcode botones | `0x37` | **`0x38`** (ZWIFT_CLICK_NOTIFICATION) |
| CH06 | "legible, posible estado DRM" | **NO existe** en el firmware V2 |

Memo original de correcciones: [`docs/protocol/CORRECTIONS_FROM_RESEARCH.md`](protocol/CORRECTIONS_FROM_RESEARCH.md).

## Criptografía (confirmada)

- **Curva:** ECDH P-256 (secp256r1). Secreto compartido = coordenada X cruda (32B).
- **HKDF:** SHA-256; `salt = devicePubKey[64] ‖ localPubKey[64]` (128B, sin prefijo 0x04);
  `info` ⚠️ contestado. El decompile (FUN_14050db90) no llama `add1_hkdf_info` y `"handshake data"`
  tiene 0 ocurrencias en el binario → la evidencia apunta a **vacío**. El código lo deja configurable
  (`HkdfInfoMode.Empty` por defecto; `--legacy-hkdf-info` para probar `"handshake data"`).
- **Salida HKDF:** 36B → `AesKey = output[0:32]`, `IvBase = output[32:36]`.
- **Cipher:** AES-256-CCM, tag 4B, AAD vacío.
- **Nonce (8B):** `IvBase[4] ‖ counter[4]` (counter little-endian).
- **Wire:** `[counter:4B LE][ciphertext][tag:4B]`.

Implementación: [`src/Crypto/ZapCrypto.cs`](../src/Crypto/ZapCrypto.cs),
[`src/Crypto/ZPEncryptionV2.cs`](../src/Crypto/ZPEncryptionV2.cs).

## BLE / GATT (confirmado)

| Característica | UUID | Rol |
|---|---|---|
| Servicio ZAP | `00000001-19CA-4651-86E5-FA29DCDD09D1` | servicio propietario (handle vivo 0x0056) |
| CH02 | `00000002-…` | Notify — telemetría/botones cifrados |
| CH03 | `00000003-…` | Write — handshake + `FF 04 00` |
| CH04 | `00000004-…` | Indicate — respuesta de handshake |
| CH100/101/102 | `00000100/0101/0102-…` | propósito abierto (candidatos a portar id/firma) |

**Enlazar siempre por UUID, nunca por handle** (los handles ATT no son estables entre conexiones).
El servicio `0xFC82` es un wrapper "Zwift Ride" secundario, no el que expone estas características.

## Opcodes (autoritativo)

Tabla completa en [`docs/protocol/opcode-catalog.md`](protocol/opcode-catalog.md). Claves:
`0x15` CONTROLLER_REQUEST, `0x19` RESET, `0x23` BATTERY_STATUS, `0x37` ZWIFT_PLAY_DEVICE_STATUS,
`0x38` ZWIFT_CLICK_NOTIFICATION, `0xFF` LOST_CONTROL. No existe opcode keep-alive/ping en ZAP.

## Login con la cuenta del usuario (diseño ético)

El `access_token` se obtiene con **la cuenta Zwift del propio usuario** (ver
[`docs/protocol/zwift-login.md`](protocol/zwift-login.md)) — nunca un token embebido:
- **password grant** con `client_id=Zwift_Mobile_Link` (tiene Direct Access Grants), o
- **refresh_token grant** con `client_id=Game_Launcher` (sin contraseña; útil con 2FA).

Variables de entorno soportadas: `ZWIFT_ACCESS_TOKEN`, `ZWIFT_USERNAME`+`ZWIFT_PASSWORD`,
`ZWIFT_REFRESH_TOKEN`. Implementación: [`src/Auth/ZwiftOAuthClient.cs`](../src/Auth/ZwiftOAuthClient.cs).

## Pendiente (solo post-unlock)

El unlock ya es completo. Lo único restante es decodificar el tráfico cifrado de CH02 tras el
unlock (botones/telemetría): derivar la clave de sesión con ECDH (priv local + campo 1 del reto
descomprimido) y zanjar el `HkdfInfoMode` con el reto en claro como oráculo. No afecta al unlock.
