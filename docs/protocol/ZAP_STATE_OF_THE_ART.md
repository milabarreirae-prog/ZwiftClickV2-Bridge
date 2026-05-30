> **Procedencia:** documento del equipo de investigación (reverse-engineering del Zwift Click V2),
> incorporado a este repositorio como referencia de protocolo. Las citas `x.c:línea` apuntan al
> decompile de `ZwiftApp.exe`, que **NO** se incluye en este repo (binario propietario de Zwift).

# 📜 Estado del Arte: Protocolo ZAP (Zwift Click V2) — Graduado por Evidencia

**Última auditoría:** 2026-05-29 (verificación adversarial multi-agente contra `x.c`, `findings/*.md`, código `.NET`, capturas BLE/HTTP).
**Reemplaza** la versión narrativa previa. Cada afirmación lleva su veredicto y cita `archivo:línea`.

## Leyenda de veredictos

| Símbolo | Veredicto | Significado |
|---|---|---|
| ✅ | **SUPPORTED** | Evidencia concreta y corroborada en el repo |
| 🟡 | **PARTIAL** | Parte verdadera; la otra parte es hipótesis o está sobre-afirmada |
| ❌ | **CONTRADICTED** | El repo contiene evidencia que contradice la afirmación |
| ⚪ | **UNSUPPORTED** | Sin evidencia en el repo (puede venir de fuentes externas/V1) |

> **Resultado global (37 afirmaciones auditadas):** 23 ✅ · 9 🟡 · 4 ❌ · 1 ⚪.
> El núcleo BLE/cripto/transporte es sólido. Los errores se concentran donde se infiltraron constantes/nombres de **Zwift Play / Click V1** (lección metodológica #1).

---

## ✅ HECHOS CONFIRMADOS

### 🔹 Topología BLE / GATT

| Elemento | Valor | Veredicto | Evidencia |
|---|---|---|---|
| Servicio ZAP | `00000001-19CA-4651-86E5-FA29DCDD09D1` (live handle 0x0056) | ✅ | phaseB-011144 §8:475; x.c:728846, 833706; BleConstants.cs:11 |
| CH02 Notify | handle `0x001B`, props 0x10 | ✅ | phaseB-011144 §8:464, §9.1:522 |
| CH03 Write | handle `0x001E`, props 0x04 (write-without-response) | ✅ | phaseB-011144 §8:465, §9.1:518, §10:555 |
| CH04 Indicate | handle `0x0020`, props 0x22 (indicate **+ read**, no notify) | ✅ | phaseB-011144 §8:466, §9.1:523 |
| CH100/101/102 | handles `0x0023/0x0027/0x002B`, UUIDs `...0100/0101/0102` | ✅ | phaseB-011144 §8:467-469; BleConstants.cs:24-30 |
| CH06 | **NO existe** en firmware V2 del dispositivo | ✅ | phaseB-011144 §8 (ausente); INDEX 11.1 |
| Advertising Manufacturer ID | `0x094A` | ✅ | x.c:727280 (`sVar10 == 0x94a`); BleConstants.cs:76 |

> **Nota CH06:** el binario `ZwiftApp.exe` **sí** declara el UUID `00000006-...` (x.c:728867), pero el **dispositivo V2** no lo expone. La distinción app-vs-firmware es la base del veredicto REFUTADO.
> **Nota props CH04:** props `0x22` = Indicate (0x20) + **Read** (0x02), no "Notify" — el bit 0x02 es READ en el bitfield GATT.

### 🔹 Criptografía

| Parámetro | Valor | Veredicto | Evidencia |
|---|---|---|---|
| Curva | ECDH P-256 (secp256r1) | ✅ | crypto_final_parameters.md:10; CryptoConstants.cs:11; phaseA3 §3 |
| HKDF hash | SHA-256 | ✅ | x.c:825076-825077 (`set_hkdf_md`) |
| HKDF salt | `devicePubKey[64] ‖ localPubKey[64]` (128B, sin 0x04), **device-first** | ✅ | x.c:825060-825061 (copia device, luego local); len 0x80 en x.c:825083 |
| HKDF output | 36 bytes (0x24) | ✅ | x.c:825383 (resize 0x24), guard 825396 "Key length out of range" |
| Key / IV split | key = output[0:32]; ivBase = output[32:36] | ✅ | x.c:825395 (`*(... )-4` = últimos 4B); CryptoConstants.cs:67-81 |
| Nonce | 8B = `ivBase[4] ‖ counter[4]` (counter little-endian) | ✅ | x.c:825493-825501 (IVLEN=8); decrypt 816310-816315 |

### 🔹 Handshake y transporte ATT

| Flujo | Valor | Veredicto | Evidencia |
|---|---|---|---|
| TX transporte | ATT Write_Command (`0x52`) → handle `0x001E` (CH03) | ✅ | phaseB-011144 §9.1/§10 seq 1751; §7.1 (20 hits) |
| RX handshake | ATT Handle_Value_Indication (`0x1D`) en `0x0020` (CH04) | ✅ | phaseB-011144 §9.1 seq 1767; §7.1 (13 hits) |
| RX stream | ATT Handle_Value_Notification (`0x1B`) en `0x001B` (CH02) | ✅ | phaseB-011144 §9.1 seq 1764; §7.1 (332 hits) |

### 🔹 Opcodes (catálogo autoritativo `x.c:814440-814690`)

| Opcode | Nombre | Veredicto | Evidencia |
|---|---|---|---|
| `0x38` | ZWIFT_CLICK_NOTIFICATION (refuta V1 `0x37`) | ✅ | x.c:814635; opcode-catalog.md:13,50 |
| `0xFF` | LOST_CONTROL; `FF 04 00` es **outbound** host→CH03 | ✅ | x.c:814698; phaseB-011144:562 (TX seq 2073) |
| Keep-Alive | **NO existe** (Ping/Pong son ZOP server, no ZAP) | ✅ | opcode-catalog.md:82; INDEX 4.6 |
| `0x23` | BATTERY_STATUS | ✅ (binario) | x.c:814599 — ⚠️ ver corrección de código abajo |

### 🔹 Auth / DRM / Red

| Aspecto | Veredicto | Evidencia |
|---|---|---|
| `/api/d-lock-service` existe en strings | ✅ | static-strings-hits.md:60 (Patcher.dll), :88 (ZwiftApp.exe) |
| `ZpHwAuthenticationEvent.device_id` | ✅ | static-strings-hits.md:159, :288; RTTI :291 |
| String "Device challenge failed: Network service not initialized" | ✅ | x.c:824411; en el handler de challenge FUN_14050d1d0 |
| Transporte WinHTTP nativo | ✅ | FINAL_TOKEN_FLOW.md:29; trazas ETL phaseB |
| `.NET 8` compila + 5/5 crypto tests | ✅ | verificado en vivo (`dotnet build` + `--test-crypto`) |

---

## 🟡 PARCIALES (verdad a medias / sobre-afirmaciones)

| # | Afirmación | Veredicto | Matiz | Evidencia |
|---|---|---|---|---|
| Wire format | `[Ciphertext][Tag 4B]` | 🟡 | **Incompleto:** hay **contador líder de 4B** → `[4B counter][ciphertext][4B tag]` | x.c:825521-825545; AGENTS.md:64; INDEX 3.1 |
| Cipher | AES-256-CCM / tag 4B / AAD vacío | 🟡 | Fuertemente indicado (key 32B, tag-before-decrypt, IVLEN=8), pero el **OID del cipher es opaco** en x.c | x.c:816317-816327 (patrón CCM); ZAP_PROTOCOL_REFERENCE.md:459 |
| Response prefix | device→host = `01 03` | 🟡 | V2 real = **`02 03`** (`01 03` es V1). Pubkey 72B **no observada** (decoder first-fragment-only) | phaseB-011144 §10 seq 1767; BleConstants.cs:40-47 |
| `0x23` BATTERY | =BATTERY_STATUS | 🟡 | Cierto en binario, **pero el código lo tenía mal** (ver correcciones) | x.c:814599; opcode-catalog.md:17 |
| `0x3C` | SPINDOWN_NOTIFICATION reusado | 🟡 | El nombre SPINDOWN es V1; en V2 es **wrapper status/config** (SPINDOWN rechazado) | x.c:814647; phaseB-015031 §18 |
| FC82 | wrapper "secundario/fallback" | 🟡 | Presente y "secundario" ✅, pero **"fallback" sin evidencia**; es el **servicio Zwift Ride** | x.c:799525; los únicos "fallback" son `BleWin10Lib_fallback.dll` |
| Daily unlock | TTL ~24h | 🟡 | "requiere app/challenge" ✅, pero **no hay literal de TTL** (142 hits = falsos positivos) | INDEX 10.1; static-strings-hits.md:1489 |
| Gate `+0x508` | en `ZapMessageComponent` | 🟡 | Gate y string reales, pero el flag vive en **`ZP_DeviceComponent`** (atribución conflada) | x.c:822172-822173; phaseA3 §5 |
| Error `58 02` | "rechazo DRM (DRM activo)" | 🟡 | Recurrencia ✅, pero "DRM" es **1 de 4 hipótesis**, no probada | unlock_token_analysis.md:105-126; INDEX 10.2 |

---

## ❌ CONTRADICHO (el repo dice lo contrario — corregido)

| # | Afirmación previa | Veredicto | Realidad según el repo | Evidencia |
|---|---|---|---|---|
| **HKDF info** | `info = "handshake data"` (14B) | ❌ | El decompile V2 (`FUN_14050db90`) **no llama `add1_hkdf_info`** y `"handshake data"` tiene **0 hits en x.c** → **info vacío**. Es herencia V1/jat255. **Necesita validación hardware.** | x.c:825070-825101; hkdf_and_validation_findings.md §A3; ZAP_PROTOCOL_REFERENCE.md:121 |
| **Handshake request** | `RideOn + 01 02 + localPubKey[64]` (72B) | ❌ | Wire real = `RideOn + 02 03` (8B) en CH03; el `01 02` es V1. (Cita `x.c:728846-728887` en `crypto_final_parameters.md` es **falsa** — es solo la tabla de UUIDs) | phaseB-011144 §9.1/§10 seq 1751 |
| **Component-ID framing** | "refutado; `0x13` = HCI Number Of Completed Packets" | ❌ | **Ningún reporte del repo lo refuta.** El más reciente (phaseB-015031 §6.3/§15) lo trata como hipótesis ZapMessageComponent **viva**, y el estático lo afirma (x.c:824562 → RTTI ZapMessageComponent). La explicación HCI es plausible pero **no es un hallazgo registrado** | phaseB-015031 §6.3,§15; phaseA3 §7 |
| **WinHTTP** | "ignora el proxy del sistema" | ❌ | El MITM del propio repo **depende de que ZwiftApp HONRE** el proxy WinHTTP; el binario implementa `no_proxy`/`Proxy-Connection` | zwift_mitm.py:18; x.c:3501452-3501456 |

---

## ⚪ SIN SUSTENTO

| Afirmación | Veredicto | Nota | Evidencia |
|---|---|---|---|
| Botones: `ClickKeyPadStatus`, `Button_Plus=1`/`Button_Minus=2`, `0=pressed/1=released` | ⚪ | **0 ocurrencias** en todo el repo. Solo existe `RideKeyPadStatus` (Play/Ride). El único formato de botón Click documentado es el byte-offset V1 sobre el opcode `0x37` (refutado). Probable origen externo (Zwift Play) | Grep en x.c/findings/*.cs/*.md; ZAP_PROTOCOL_REFERENCE.md:217,220 |

---

## 🛠️ Correcciones de código aplicadas (2026-05-29)

A raíz de esta auditoría se corrigieron bugs reales en el bridge `.NET`:

| Archivo | Cambio | Motivo |
|---|---|---|
| `BleConstants.cs` | `0x23`→BATTERY_STATUS, `0x19`→RESET, `0x15`→CONTROLLER_REQUEST, `0x08`→PLAY_NOTIF, `0x3C`→SPINDOWN/wrapper; eliminada `RideNotificationType` | Valores V1 mal mapeados (x.c:814440-814690) |
| `ButtonNotifyProbe.cs` | Parseo actualizado a los nombres corregidos | Consistencia con catálogo |
| `CryptoConstants.cs` / `ZPEncryption.cs` | `HkdfInfo` marcado ⚠️ CONTESTED (valor sin cambiar) | Conflicto info vacío vs "handshake data" |
| `ZapPayload.cs` | `KeepAlive()`→`ControllerRequest()` | 0x15 no es keep-alive |

> ⚠️ **El cambio crítico pendiente** es el `info` HKDF: si el descifrado contra un Click V2 real falla, probar `info` **vacío** ANTES que cualquier otra cosa. Los self-tests no pueden decidirlo (usan vectores placeholder).

---

## 🔍 Próximos pasos (todo requiere hardware / captura en vivo)

### 🟢 Inmediato (hardware + unlock)
- [ ] Round-trip de descifrado contra Click V2 real → **zanjar `info` HKDF** (vacío vs "handshake data") y confirmar **AES-256-CCM** (OID opaco en estático).
- [ ] Confirmar `0x38` ClickKeyPadStatus/notificación en CH02 en vivo y mapear su estructura real (no asumir nombres de Play).
- [ ] Validar prefijo `02 03` y capturar los **fragmentos de continuación** con las pubkeys de 64B.

### 🟡 Red / Auth challenge
- [ ] Capturar WinHTTP (ETW `Microsoft-Windows-WinHTTP` / proxy) durante Hot Pairing → payload de `/api/d-lock-service` / `/api/auth`.
- [ ] Determinar algoritmo de firma y vinculación (cuenta vs dispositivo).

### 🟠 Firmware / canales
- [ ] Caracterizar CH100/101/102 (ver `plan_fc82_char_probe.md`).
- [ ] Matriz de errores del handshake (más allá de `58 02`).

---

## 🧭 Lección metodológica central

Las **4 contradicciones + las peores parciales** comparten una sola raíz: **constantes/nombres de V1/Play infiltrados** (`handshake data`, `01 02`/`01 03`, `0x37`, `0x23`→Ride, `ClickKeyPadStatus`, `0x15`→keep-alive). La trampa V1≡V2 sigue activa **dentro del propio código y de los docs derivados**, no solo en las notas externas. Regla: **toda afirmación que reuse un nombre/valor V1 o Play se trata como sospechosa hasta verla en `x.c` o en captura V2.**

> Refutado transversal: el "wrapper `04 00`" como capa wire **no existe** (coincidencia hex, INDEX 4.1). Cualquier afirmación que descanse en él está muerta.
