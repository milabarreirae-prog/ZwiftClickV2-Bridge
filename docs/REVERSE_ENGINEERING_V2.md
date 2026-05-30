# Ingeniería inversa — Zwift Click V2 (ZAP)

> **Estado: RESUELTO en lo esencial.** El mecanismo de bloqueo y la cadena de unlock están
> confirmados por captura BLE+HTTP correlacionada sobre la app oficial. Este documento es el
> resumen; la referencia de protocolo graduada por evidencia vive en
> [`docs/protocol/`](protocol/README.md).

## TL;DR

El Click V2 no se desbloquea localmente. Su bloqueo es **DRM con respaldo de servidor**: la app
oficial valida la cuenta/dispositivo contra el servidor de Zwift (con el `access_token` de la cuenta
del usuario) y solo entonces confirma el unlock por BLE. La cadena completa:

```
1. BLE   handshake  "RideOn 02 03" + localPubKey[64]      (write CH03 ↔ indicate CH04)
2. HTTP  POST /api/d-lock-service/device/authenticate      Authorization: Bearer <access_token>
         body protobuf { 1: devicePubKey_comprimida(33B), 2: id, 3: firma(40B) }   → 204 No Content
3. BLE   write "FF 04 00" → CH03                           (11 ms después del 204)
4.       sesión cifrada AES-256-CCM fluye en CH02
```

Detalle y evidencia: [`docs/protocol/unlock-flow.md`](protocol/unlock-flow.md).

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

## Lo único que falta para un unlock de extremo a extremo

El **origen de los campos 2 (id) y 3 (firma 40B)** del request `device/authenticate`. El campo 1
(pubkey) sale del handshake; los campos 2 y 3 los emite el dispositivo por BLE y su ruta exacta no
está resuelta. El código deja el punto de ensamblado listo
([`ZwiftClickBridge.TryAssembleChallenge`](../src/Bridge/ZwiftClickBridge.cs)) y no fabrica un
request inválido mientras tanto. Ver [`docs/protocol/unlock-flow.md`](protocol/unlock-flow.md).
