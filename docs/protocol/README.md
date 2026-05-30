# Referencia de protocolo ZAP (Zwift Click V2)

Documentación de protocolo derivada del trabajo de ingeniería inversa del **Zwift Accessory
Protocol (ZAP)** usado por el Zwift Click V2 (2025). Cada afirmación está graduada por evidencia.

| Documento | Contenido |
|---|---|
| [ZAP_STATE_OF_THE_ART.md](ZAP_STATE_OF_THE_ART.md) | Estado del arte graduado por evidencia (✅/🟡/❌/⚪). **Empieza aquí.** |
| [unlock-flow.md](unlock-flow.md) | El flujo de unlock server-backed confirmado (handshake → POST d-lock → `FF 04 00`). |
| [opcode-catalog.md](opcode-catalog.md) | Catálogo autoritativo de opcodes de wire ZAP. |

## Resumen cripto/transporte (confirmado)

| Concepto | Valor |
|---|---|
| Servicio BLE | `00000001-19CA-4651-86E5-FA29DCDD09D1` (enlazar por UUID, no por handle) |
| CH02 Notify (device→host) | `00000002-…` — telemetría/botones cifrados |
| CH03 Write (host→device) | `00000003-…` — handshake + `FF 04 00` |
| CH04 Indicate (device→host) | `00000004-…` — respuesta de handshake |
| Handshake | `"RideOn" 02 03 + pubKey[64]` (prefijo V2 = `02 03`) |
| Curva | ECDH P-256; secreto = X cruda (no SHA256(X)) |
| HKDF | SHA-256; salt = `devicePubKey[64] ‖ localPubKey[64]` (128B); info ⚠️ contestado (probablemente vacío) |
| Derivación | output 36B → AesKey = [0:32], IvBase = [32:36] |
| Cipher | AES-256-CCM, tag 4B, AAD vacío |
| Nonce | `IvBase[4] ‖ counter[4]` (counter LE) |
| Wire | `[counter:4B LE][ciphertext][tag:4B]` |
| Opcode botones Click | `0x38` (ZWIFT_CLICK_NOTIFICATION) |

## Lo que NO está en este repositorio (por diseño)

- El decompile `x.c` de `ZwiftApp.exe` ni ningún binario de Zwift (propietario).
- Capturas crudas (`out/mitm/`, `phaseC-captures/`): contenían **tokens OAuth reales** y datos del
  dispositivo. Quedan fuera del repo; aquí solo vive el conocimiento de protocolo sanitizado.

## Incógnita abierta

El origen de los campos **2 (id)** y **3 (firma 40B)** del request `device/authenticate`. Ver
[unlock-flow.md](unlock-flow.md). Es el único bloqueo para un unlock de extremo a extremo.
