> **Procedencia:** hallazgo del equipo de investigación, **validado de extremo a extremo en
> hardware** (Click V2, 2026-05-30) con MITM HTTPS + ETW BLE correlacionados.
> **Sanitizado para publicación:** los valores concretos del dispositivo (clave pública, id y firma)
> y cualquier token OAuth están **redactados**. Las capturas crudas (`out/mitm/`, `phaseC-captures/`)
> **NO** se incluyen en este repositorio.

# Flujo de unlock del Zwift Click V2 (server-backed) — RESUELTO ✅

El bloqueo del Click V2 es **DRM con respaldo de servidor**, y la cadena completa ya se reprodujo
**sin la app oficial**. No hay unlock local: hace falta el `access_token` de la cuenta Zwift del
usuario y una validación del servidor. Lo central del hallazgo:

> **El reto lo genera el propio dispositivo y lo emite EN CLARO por CH02.** No hay que construir ni
> firmar nada: el bridge captura el blob, le quita el header de 3 bytes y **reenvía los 82 bytes
> verbatim** al servidor con el Bearer del usuario.

## La secuencia (validada en vivo)

```
1. BLE   handshake  "RideOn 02 03" + localPubKey[64]  (72B) → CH03 (write-without-response)
         (La indicación del device en CH04 puede ser "RideOn 02 03 58 02 00…" = trama de estado.
          EL 58 02 NO ES FATAL: el reto llega igual por CH02.)
2. BLE   El DISPOSITIVO emite en CH02 (notify) una trama PLAINTEXT de ~85B:
            FF 03 00 | <protobuf 82B>
         protobuf = { 1: pubkey_comprimida(33B, 02/03‖X), 2: id(varint), 3: firma(40B) }
3. HTTP  POST https://us-or-rly101.zwift.com/api/d-lock-service/device/authenticate
            Authorization: Bearer <access_token de la cuenta del usuario>   (sin Content-Type)
            Body = esos 82 bytes VERBATIM (quitando el header FF 03 00)
         → 204 No Content
4. BLE   write "FF 04 00" → CH03   (señal de unlock; solo válida tras el 204)
5. BLE   sesión cifrada AES-256-CCM fluye en CH02
```

## Anatomía del reto (campos)

| Campo | Wire | Tamaño | Significado | Naturaleza |
|---|---|---|---|---|
| 1 | bytes (tag `0A`, len `21`) | 33 B | pubkey EC del device, comprimida (`0x02/0x03 ‖ X`) | **efímera** por sesión |
| 2 | varint (tag `10`) | — | identificador del dispositivo | **estático** (mismo en todas las sesiones) |
| 3 | bytes (tag `1A`) | 40 B | firma / prueba de challenge | fresca por sesión |

> Valores de ejemplo **redactados**: `1: 03 <…X de 32B…>`, `2: <DEVICE_ID>`, `3: <FIRMA de 40B>`.
> El blob completo en CH02 es `FF 03 00` ‖ (esos 82B). El cuerpo del POST empieza en el offset 3.

## Conclusiones (confirmadas en hardware)

1. **DRM server-backed, 100% confirmado.** Requiere `access_token` de la cuenta + round-trip al
   servidor. Un cliente propio reprodujo el `204` y el unlock completo sin la app oficial.
2. **Los tres campos los genera el dispositivo.** El bridge NO construye id ni firma: reenvía los
   82B verbatim. Esto cierra el antiguo "gran desconocido" del origen de los campos 2 y 3.
3. **El reto es PLAINTEXT en CH02** → no se necesita la cripto de sesión para desbloquear. La cripto
   (AES-256-CCM) solo hace falta para decodificar botones/telemetría DESPUÉS del unlock.
4. **`FF 04 00` es la confirmación de unlock**, válida solo tras el `204`.
5. **`58 02` no es un rechazo fatal**: es una trama de estado en CH04; el reto llega igual.
6. La pubkey EC del dispositivo (para derivar la sesión) se recupera **descomprimiendo el campo 1**
   del reto (X + paridad → Y); su clave estática nunca aparece en claro por BLE.

## Implementación en este repo

- Parseo del reto: [`src/Auth/DeviceAuthChallenge.cs`](../../src/Auth/DeviceAuthChallenge.cs) (`TryParse`).
- POST verbatim + Bearer: [`src/Auth/DeviceUnlockClient.cs`](../../src/Auth/DeviceUnlockClient.cs).
- Login con la cuenta del usuario: [`src/Auth/ZwiftOAuthClient.cs`](../../src/Auth/ZwiftOAuthClient.cs) (ver [zwift-login.md](zwift-login.md)).
- Orquestación BLE: [`src/Bridge/ZwiftClickBridge.cs`](../../src/Bridge/ZwiftClickBridge.cs).

## Cripto de sesión post-unlock (auto-resuelta por bake-off)

Decodificar CH02 cifrado tras el unlock requiere zanjar dos parámetros que el estático dejó abiertos:
el modo de derivación ECDH (X cruda vs SHA256) y el `info` de HKDF (vacío vs `"handshake data"`). En
vez de adivinar, el bridge construye los **4 candidatos** y deja que la **validez del tag AES-CCM**
sea el oráculo: el candidato que descifra limpiamente la primera notificación real de CH02 es el
correcto (clave equivocada → tag inválido → descarte). La pubkey del dispositivo se obtiene
descomprimiendo el campo 1 del reto (X + paridad → Y).

Implementación: [`src/Crypto/SessionKeyBakeoff.cs`](../../src/Crypto/SessionKeyBakeoff.cs), integrado en
el bridge (se resuelve solo con el tráfico real). Equivale al `--hkdf-bakeoff` del probe de
investigación. **Esto no afecta al unlock** (que es en claro); solo a la decodificación de botones.
