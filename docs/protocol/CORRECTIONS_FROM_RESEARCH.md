# Correcciones desde el equipo de investigación (Decompiled/)

> ✅ **APLICADAS (documento histórico).** Todas las correcciones de cripto/transporte de abajo ya
> están aplicadas en el código (AES-256-CCM, salt 128B, ECDH raw, prefijo `02 03`, opcodes, sin CH06).
> Hallazgos posteriores que también se incorporaron:
> - **El reto del d-lock lo genera el dispositivo y lo emite EN CLARO por CH02** (`FF 03 00 ‖ 82B`);
>   el bridge lo reenvía **verbatim** (no construye los campos 2/3). Ver [unlock-flow.md](unlock-flow.md).
> - **`58 02` NO es fatal**: es una trama de estado en CH04; el reto llega igual.
> - **Login** con la cuenta del usuario: password grant `Zwift_Mobile_Link`, refresh `Game_Launcher`.
>   Ver [zwift-login.md](zwift-login.md).

> **De:** equipo de research (líder de protocolo) · **Para:** equipo constructor (ZwiftClickV2-Bridge)
> **Fecha:** 2026-05-29 · **Base:** auditoría adversarial del decompile `x.c` + capturas ETW/MITM + corrida en hardware
> **Resumen:** Vuestro `REVERSE_ENGINEERING_V2.md` tiene varios puntos cripto **incorrectos** (algunos contradichos por vuestro propio código). Aquí van las correcciones con evidencia, y la confirmación de que **el muro es el DRM**, no el wire format.

---

## 0. Confirmación conjunta: el bloqueo es DRM (58 02), no el formato

Ambos equipos, en hardware, obtenemos lo mismo: al enviar cualquier handshake estructurado por CH03, el dispositivo responde
`RideOn 02 03 58 02 00 00 00 00 00 01 FB 00…` (72B) y **nunca devuelve su pubkey EC**. `58 02` = protobuf field 11 = 2 (rechazo auth).
Probamos **exactamente** vuestro formato `RideOn 02 03 + pubkey[64]` (sin 0x04) → mismo `58 02`. Conclusión compartida: el firmware **rehúsa el ECDH hasta estar provisionado** por la app oficial vía un *auth challenge* resuelto por red. El formato del handshake NO es el problema.

(Dato extra: también llega un NOTIFY sin cifrar en CH02 `08 00 10 64 18 …` = batería 100% incluso bloqueado.)

---

## 1. Cripto: correcciones (evidencia del decompile)

| Tema | Vuestro doc | Correcto (x.c) | Evidencia |
|---|---|---|---|
| **Cipher** | AES-128-**GCM** | AES-256-**CCM** | El argumento "no existe `EVP_aes_128_ccm`" es **inválido**: el cipher se selecciona vía puntero `EVP_CIPHER` **opaco** (`FUN_1411abdc0` → `DAT_141bc6e10`); los literales `aes_*_gcm` que visteis están en una **tabla TLS/QUIC no relacionada** (x.c:~4720484). La ruta ZAP usa hallmarks CCM: `SET_TAG` (ctrl 0x11) **antes** de descifrar (x.c:816317), pase de priming de longitud CCM `FUN_1411a6c90(ctx,0,len,0)` (x.c:816327) sin AAD-Update, e IVLEN=8 (x.c:816315). |
| **Tamaño de clave** | 16B (AES-128) | **32B (AES-256)** | El HKDF output de 36B se parte `key[0:32] ‖ ivBase[32:36]`. x.c:825395 toma los **últimos 4 bytes** como IV base (`*(...)-4`); los 32 previos son la clave. Vuestro split 16+16+4 es el layout V1. |
| **HKDF salt** | doc dice 96B (`peer ‖ SHA256(peer)`) | **128B `device_pub[64] ‖ local_pub[64]`** | x.c:825060-825061 (dos memcpy: device primero, luego local) + len 0x80 en x.c:825083. **Vuestro propio código `ZapCrypto.cs:28` ya usa 128B device‖local** — corregid el doc, no el código. |
| **HKDF info** | doc dice vacío / código dice `"handshake data"` | Decompile sugiere **vacío** (sin `add1_hkdf_info`; `"handshake data"` = 0 hits en x.c) | x.c:825070-825101. **Sin resolver: validar contra hardware.** Si el descifrado falla, probad `info` vacío. |

> **Importante:** ninguna de estas cuatro es validable sin una sesión viva (clave de sesión), y eso lo bloquea el DRM. Pero construir sobre AES-128-GCM/salt-96B es construir sobre arena: el decompile dice AES-256-CCM/salt-128B.

---

## 2. Handshake V2

- Prefijo correcto = **`02 03`** (dos bytes), clave **raw de 64B sin `0x04`**. Vuestro enum ya tiene `V2_RideOn_02_03_Key64` — ese es el correcto; el resto son V1/ruido.
- El "header binario de 7 bytes + 0x04 forzado" es una interpretación V1. La app oficial escribe `RideOn 02 03 + pubkey[64]` en CH03 (confirmado en captura ETW y reproducido por nosotros).
- Da igual: con DRM activo, **todos** dan `58 02`.

## 3. CH06 — contradicción abierta

Vuestro doc afirma que CH06 (`00000006`) es legible y podría tener estado DRM. Nuestra discovery ATT **no encontró** `00000006` (lo marcamos REFUTADO). Es testeable: tenemos un probe `--read-ch06` que enumera todas las chars y lee las legibles. **Si CH06 existe y devuelve un blob, sería un hallazgo importante.** Reportaremos el resultado.

## 4. Handles GATT no son estables

Observado: en una corrida CH02/03/04 salieron en handles `0x001A/1D/1F`; en otra captura `0x001B/1E/20`. **Enlazad siempre por UUID, nunca por handle.**

---

## 5. El camino real (y ya tenemos la herramienta)

No hace falta un sniffer hardware de $120. **Ya tenemos MITM HTTPS funcionando** contra Zwift (`forensic_scripts/zwift_mitm.py` + cert CA instalado): hoy capturamos el flujo OAuth completo (`/api/auth` + token Keycloak → `access_token`). El addon ya vigila `/api/d-lock-service`, `/challenge`, `/device/auth`, `/provision` como HIGH-INTEREST.

**Falta solo correrlo durante el hot-pairing del Click V2 con Zwift oficial.** Eso debería capturar el *device auth challenge/response* (con `Authorization: Bearer <access_token>`) — la pieza que falta para pasar el `58 02`. Esa request/response es lo que hay que reinyectar en la capa ZAP.

> Pasos: `mitmweb --listen-port 8080 -s forensic_scripts/zwift_mitm.py` → `netsh winhttp set proxy 127.0.0.1:8080` → abrir Zwift y emparejar el Click → revisar `out/mitm/*_request.json` / `*_response.json` HIGH-INTEREST → `netsh winhttp reset proxy`.

Fuente autoritativa de protocolo: `../Decompiled/ZAP_STATE_OF_THE_ART.md` (graduado por evidencia) y `../Decompiled/findings/INDEX.md`.
