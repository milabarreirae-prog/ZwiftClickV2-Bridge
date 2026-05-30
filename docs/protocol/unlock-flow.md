> **Procedencia:** hallazgo del equipo de investigación, capturado con MITM HTTPS + ETW BLE
> correlacionados sobre la app oficial de Zwift y el propio Click V2 del investigador.
> **Sanitizado para publicación:** los valores concretos del dispositivo (clave pública, id y
> firma) y cualquier token OAuth han sido **redactados**. Las capturas crudas (`out/mitm/`,
> `phaseC-captures/`) **NO** se incluyen en este repositorio.

# Flujo de unlock del Zwift Click V2 (server-backed) — CONFIRMADO

El bloqueo del Click V2 es **DRM con respaldo de servidor**. No hay unlock local: hace falta el
`access_token` de una cuenta Zwift y una validación del servidor. La secuencia completa se capturó
con correlación de milisegundos entre BLE (ETW) y HTTP (MITM):

```
T+0.000  BLE  TX host→CH03:  52 69 64 65 4F 6E 02 03 …   "RideOn 02 03" + localPubKey[64]  (handshake)
T+0.085  BLE  RX device→CH04: 52 69 64 65 4F 6E 02 03 …   "RideOn 02 03"  (respuesta limpia, SIN 5802)
T+4.544  HTTP POST …/api/d-lock-service/device/authenticate   →  204 No Content
T+4.555  BLE  TX host→CH03:  FF 04 00                        ← 11 ms DESPUÉS del 204
T+4.5xx  BLE  RX device→CH02: telemetría/batería cifrada (sesión desbloqueada, AES-256-CCM)
```

## La llamada de unlock

```
POST https://us-or-rly101.zwift.com/api/d-lock-service/device/authenticate
Authorization: Bearer <access_token de la cuenta Zwift del usuario>
Body: 82 bytes, protobuf
Respuesta: HTTP 204 No Content (cuerpo vacío)
```

### Cuerpo del request (protobuf)

| Campo | Wire | Tamaño | Significado | Origen |
|---|---|---|---|---|
| 1 | bytes (tag 0x0A) | 33 B | **Clave pública EC del dispositivo, COMPRIMIDA** (`0x02/0x03 ‖ X`) | del handshake BLE |
| 2 | varint (tag 0x10) | — | **Identificador del dispositivo** | lo genera el dispositivo |
| 3 | bytes (tag 0x1A) | 40 B | **Firma / prueba de challenge** del dispositivo | lo genera el dispositivo |

> Valores de ejemplo **redactados**: campo 1 = `03 <…X de 32B…>`, campo 2 = `<DEVICE_ID>`,
> campo 3 = `<FIRMA de 40B>`.

## Conclusiones

1. **El DRM es server-backed (100% confirmado).** El unlock requiere un `access_token` válido de
   una cuenta Zwift (Bearer) y un round-trip al servidor. Esto explica el `58 02` que ve un cliente
   no autorizado.
2. **El servidor responde `204 No Content`** — NO devuelve un ticket reinyectable. Es validación del
   lado servidor: "¿esta cuenta puede usar este dispositivo?".
3. **`FF 04 00` es el comando de unlock**, pero el dispositivo solo lo acepta **después del 204**.
   Cierra la vieja hipótesis de "unlock local/haptic": `FF 04 00` es la confirmación BLE que la app
   escribe una vez que el servidor autorizó la cuenta/dispositivo.
4. Los `58 02` que se perseguían a nivel ATT eran en gran parte **artefactos de framing HCI**; un
   cliente propio recibe un rechazo ATT real porque nunca hace la auth de servidor.

## Lo que necesita un bridge de terceros (ético)

1. Obtener un `access_token` OAuth de **la propia cuenta Zwift del usuario** (login con su cuenta).
2. BLE: handshake `RideOn 02 03` con el dispositivo (write CH03, indicate CH04).
3. HTTP: `POST …/api/d-lock-service/device/authenticate` con `Authorization: Bearer <token>` y el
   protobuf `{1: pubkey_comprimida, 2: id, 3: firma}` → esperar `204`.
4. BLE: escribir `FF 04 00` en CH03.
5. El dispositivo queda desbloqueado → fluye la sesión cifrada ZAP (AES-256-CCM).

## ⚠️ Desconocido que aún bloquea la implementación completa

El **origen exacto de los campos 2 (id) y 3 (firma de 40B)**. El campo 1 sale del handshake BLE;
los campos 2 y 3 los emite el dispositivo y la app los lee por BLE (¿handshake en fragmentos de
continuación? ¿CH100/101/102?). Hasta resolverlo no se puede generar un request `authenticate`
válido. Pistas en el decompile: `FUN_14050d1d0` ("Received auth challenge"), `FUN_14050a3a0`,
`ZpHwAuthenticationEvent`.

## Pendiente (menor prioridad)

- Esquema de la firma (campo 3, 40B): qué algoritmo y qué firma (¿nonce de challenge del dispositivo?).
- Verificar la pubkey comprimida (campo 1) contra la pubkey de 64B del handshake BLE.
- Confirmar si `FF 04 00` es necesario en cada sesión o solo en el primer unlock diario.
