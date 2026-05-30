> **Procedencia:** documento del equipo de investigación (reverse-engineering del Zwift Click V2),
> incorporado a este repositorio como referencia de protocolo. Las citas `x.c:línea` apuntan al
> decompile de `ZwiftApp.exe`, que **NO** se incluye en este repo (binario propietario de Zwift).

# ZAP Opcode Catalog (Authoritative)

**Source:** `x.c:814440-814690`, function `FUN_140500800` (opcode→name table initializer using `FUN_14050b620(name, &key)`).

This is the **authoritative wire opcode → symbolic name mapping** as registered by `ZwiftApp.exe`. The previously documented mapping (jat255 V1 constants) is **partially wrong for Click V2** — most notably:

| Previously believed (jat255 V1) | Confirmed (V2 binary) |
|---|---|
| `0x07` = CONTROLLER_NOTIFICATION | `0x07` = **TRAINER_CONFIG_STATUS** |
| `0x15` = EMPTY_MESSAGE | `0x15` = **CONTROLLER_REQUEST** |
| `0x19` = BATTERY_LEVEL | `0x19` = **RESET** |
| `0x37` = CLICK_NOTIFICATION | `0x37` = **ZWIFT_PLAY_DEVICE_STATUS** |
| — | `0x38` = **ZWIFT_CLICK_NOTIFICATION** (the real one) |
| — | `0x23` = **BATTERY_STATUS** |
| — | `0x28` = **CONTROLLER_NOTIFICATION** |

The empirical battery payload `08 00 10 64 18` we observed on CH02 should therefore have been preceded by a `0x23` type byte (BATTERY_STATUS), not `0x19`.

## Full table

| Hex | Dec | Symbolic name |
|----:|----:|---|
| `0x01` | 1   | `Zp_Opcode_GET` |
| `0x03` | 3   | `Zp_Opcode_DEV_INFO_STATUS` |
| `0x04` | 4   | `Zp_Opcode_TRAINER_NOTIF` |
| `0x05` | 5   | `Zp_Opcode_TRAINER_CONFIG_SET` |
| `0x07` | 7   | `Zp_Opcode_TRAINER_CONFIG_STATUS` |
| `0x08` | 8   | `Zp_Opcode_ZWIFT_PLAY_NOTIF` |
| `0x0c` | 12  | `Zp_Opcode_DFU_START` |
| `0x0f` | 15  | `Zp_Opcode_DEV_INFO_SET` |
| `0x12` | 18  | `Zp_Opcode_POWER_OFF` |
| `0x15` | 21  | `Zp_Opcode_CONTROLLER_REQUEST` |
| `0x16` | 22  | `Zp_Opcode_DIAG_EVENT_DATA_READY` |
| `0x17` | 23  | `Zp_Opcode_DIAG_DATA_STATUS` |
| `0x18` | 24  | `Zp_Opcode_TRAINER_DIAG_STATUS` |
| `0x19` | 25  | `Zp_Opcode_RESET` |
| `0x1a` | 26  | `Zp_Opcode_BATTERY_NOTIF` |
| `0x23` | 35  | `Zp_Opcode_BATTERY_STATUS` |
| `0x28` | 40  | `Zp_Opcode_CONTROLLER_NOTIFICATION` |
| `0x29` | 41  | `Zp_Opcode_GAME_STATE` |
| `0x2a` | 42  | `Zp_Opcode_DFU_START_STATUS` |
| `0x2b` | 43  | `Zp_Opcode_LOG_DATA` |
| `0x2c` | 44  | `Zp_Opcode_FAULT_DATA` |
| `0x2d` | 45  | `Zp_Opcode_ZWIFT_PLAY_SETTINGS_STATUS` |
| `0x2e` | 46  | `Zp_Opcode_ZWIFT_PLAY_SETTINGS_SET` |
| `0x2f` | 47  | `Zp_Opcode_ZWIFT_HUB_CALIBRATION_DATA_STATUS` |
| `0x33` | 51  | `Zp_Opcode_ZWIFT_HUB_CALIBRATION_DATA_SET` |
| `0x34` | 52  | `Zp_Opcode_ZWIFT_PLAY_BATTERY_NOTIF` |
| `0x37` | 55  | `Zp_Opcode_ZWIFT_PLAY_DEVICE_STATUS` |
| `0x38` | 56  | `Zp_Opcode_ZWIFT_CLICK_NOTIFICATION` ⭐ |
| `0x39` | 57  | `Zp_Opcode_ZWIFT_CLICK_VERSIONS_RESPONSE` |
| `0x3a` | 58  | `Zp_Opcode_ZWIFT_HUB_VERSIONS_RESPONSE` |
| `0x3b` | 59  | `Zp_Opcode_SPINDOWN_REQUEST` |
| `0x3c` | 60  | `Zp_Opcode_SPINDOWN_NOTIFICATION` |
| `0x3e` | 62  | `Zp_Opcode_GET_RESPONSE` |
| `0x3f` | 63  | `Zp_Opcode_STATUS_RESPONSE` |
| `0x40` | 64  | `Zp_Opcode_SET` |
| `0x41` | 65  | `Zp_Opcode_SET_RESPONSE` |
| `0x42` | 66  | `Zp_Opcode_LOG_LEVEL_SET` |
| `0x43` | 67  | `Zp_Opcode_DATA_CHANGE_NOTIFICATION` |
| `0x44` | 68  | `Zp_Opcode_GAME_STATE_NOTIFICATION` |
| `0x45` | 69  | `Zp_Opcode_SENSOR_RELAY_CONFIG` |
| `0x46` | 70  | `Zp_Opcode_SENSOR_RELAY_GET` |
| `0x47` | 71  | `Zp_Opcode_SENSOR_RELAY_RESPONSE` |
| `0x48` | 72  | `Zp_Opcode_SENSOR_RELAY_NOTIFICATION` |
| `0x49` | 73  | `Zp_Opcode_HRM_DATA_NOTIFICATION` |
| `0x4a` | 74  | `Zp_Opcode_WIFI_CONFIG_REQUEST` |
| `0x52` | 82  | `Zp_Opcode_WIFI_NOTIFICATION` |
| `0xfd` | 253 | `Zp_Opcode_RIDE_ON` |
| `0xfe` | 254 | `Zp_Opcode_RESERVED` |
| `0xff` | 255 | `Zp_Opcode_LOST_CONTROL` |
| (TBD) | — | `Zp_Opcode_VENDOR_MESSAGE` (declared at end of table, key not visible in current slice — see x.c:~814690+) |
| (TBD) | — | `Zp_Opcode_OPTIONS` (present in string table, registration not yet located) |

The default for unknown values is the string `"UNKNOWN ZP MESSAGE!"` (x.c:814740).

## Gap impact

- **Gap 4.1** (outgoing opcode of auth response): the table contains no symbolic name resembling "AUTH_RESPONSE" or "AUTH_CHALLENGE". Hypothesis: auth uses one of `SET`/`SET_RESPONSE` (0x40/0x41), `STATUS_RESPONSE` (0x3f), or `VENDOR_MESSAGE` (key unknown). Must be confirmed by Phase B BLE capture.
- **Gap 4.2** (button message structure): `0x38` `ZWIFT_CLICK_NOTIFICATION` is the correct wire opcode for Click V2 buttons (NOT `0x37`). Schema field names in `.rdata` confirm a separate notification flow per device type (`ZWIFT_PLAY_BATTERY_NOTIF` vs `BATTERY_NOTIF`/`BATTERY_STATUS`).
- **Gap 4.3** (other opcodes): full catalog above. `0x4b`/`0x4c`/`0x4e` from the previously-cited dispatch table do NOT appear in this name table — they may exist as raw handlers without a logged name (suggests the previous "handler" extraction in AGENTS.md was a different dispatch path).
- **Gap 4.6** (keep-alive): no `KEEP_ALIVE` or `PING`/`PONG` opcode exists in the catalog. `Ping`/`Pong` strings elsewhere belong to ZOP server protocol (HTTP/TCP), not ZAP BLE.
- **Gap 6.2** (`FF 04 00`): the leading `0xFF` matches `Zp_Opcode_LOST_CONTROL`. The subsequent `0x04` is most likely a sub-code, not the opcode. This refutes the earlier "haptic" hypothesis.
- **Gap 4.4** (85-byte packet `FF 03 00 0A 21 ...`): leading `0xFF` = `LOST_CONTROL`. The packet is likely a structured `LOST_CONTROL` event with embedded length/payload. NOT yet a separate opcode family.
- **Gap 6.1** (firmware/DFU): native DFU pipeline exists in-app (`Zp_Opcode_DFU_START=0x0c`, `Zp_Opcode_DFU_START_STATUS=0x2a`, "DFU Update", "Device in DFU mode", "Zp_Opcode_DFU_START_STATUS"); the DFU endpoint is `/api/dfu` (visible in `Patcher.dll` / `ZwiftApp.exe` as `/api/dfuK` where `K` is an adjacent byte).
- **Gap 9.1/9.2** (telemetry): explicit `ZpHwTelemetryEvent` family confirmed (`Disconnection`, `Battery`, `Fault`, `RTOS`) — Zwift collects device-side telemetry server-side via StructuredEvent.
