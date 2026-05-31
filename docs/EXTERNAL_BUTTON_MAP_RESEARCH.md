# Investigación externa — ¿ya existe un mapa de botones del Zwift Click V2?

**Fecha:** 2026-05-30
**Pregunta:** Antes de capturar firmas a mano, ¿la comunidad ya decodificó los botones del Click V2?
**Respuesta corta:** **Sí.** Existe un decodificador **determinista por bitmask** (no por firma
aprendida) publicado en repos de la comunidad. Esto cambia el diseño recomendado de Violeta.

---

## 1. Hardware confirmado

El **Zwift Click V2 (2025)** es un **juego de dos mandos de 5 botones cada uno = 10 botones**,
unidos (bridged) por **una sola conexión BLE**. Izquierdo = direccionales (navegar/dirección);
derecho = acción (seleccionar menú, atrás, PowerUps, Ride Ons). Fuentes: [Zwift Insider](https://zwiftinsider.com/click-v2-announced/),
[DC Rainmaker](https://www.dcrainmaker.com/2025/09/zwift-click-v2-in-depth-review.html),
[GPLama](https://gplama.com/2025/09/04/new-zwift-click-v2-controllers-all-the-details/).

> Esto confirma el mandato del usuario: **son 10 botones**, no 2. El tratamiento como shifter de
> 2 botones era incorrecto.

---

## 2. EL MAPA DE BOTONES YA EXISTE (determinista, por bitmask)

Repo: **`andriuz29/Zwift-Click-V2-Universal-PC-Controller`** (Python/bleak, dirigido al Click V2).
Conecta sin cifrado: escribe el handshake `RideOn 02 03` (`526964654f6e0203`) en CH03, luego
`000800` y `000810`, y a partir de ahí el mando emite los botones **en claro** en CH02
(`00000002-19ca-4651-86e5-fa29dcdd09d1`).

### Formato de la trama de botón

```
23 08 <B0> <B1> <B2> <B3> 0F        (7 bytes)
```

- Byte 0 = `0x23`: tipo de mensaje "botones del Click V2" (NUEVO; no estaba en la lista de tipos
  de ajchellew para Play: 0x07 ctrl-notif, 0x15 vacío, 0x19 batería, 0x37 click-notif).
- Byte 1 = `0x08`: tag protobuf de campo 1 (varint).
- Bytes 2–5 = **bitmask de estado de botones, ACTIVO-BAJO** (1 = suelto, 0 = pulsado).
- Byte 6 = `0x0F`: cierre del varint.

### Tabla observada (active-low: el bit en 0 indica botón pulsado)

| Trama (hex) | Byte que cambia | Botón | Tecla que asignó andriuz29 |
|---|---|---|---|
| `2308 FFFFFFFF 0F` | — | (reposo / ninguno) | — |
| `2308 FE FFFFFF 0F` | B0 bit0 (`0x01`) | **LEFT** | `←` |
| `2308 FD FFFFFF 0F` | B0 bit1 (`0x02`) | **UP** | `U` |
| `2308 FB FFFFFF 0F` | B0 bit2 (`0x04`) | **RIGHT** | `→` |
| `2308 F7 FFFFFF 0F` | B0 bit3 (`0x08`) | **DOWN** | `down` |
| `2308 FF DF FFFF 0F` | B1 bit5 (`0x20`) | **PLUS (+)** | `K` |
| `2308 FF FD FFFF 0F` | B1 bit1 (`0x02`) | **MINUS (−)** | `I` |

Notas:
- `andriuz29` solo mapeó **6** de los 10 botones (un controlador: d-pad + los dos shifters). Los
  otros 4 (acción del controlador derecho) son **bits adicionales** en B0/B1/B2/B3 sin documentar
  aún — se descubren pulsándolos y viendo qué bit baja.
- La asignación de teclas (PLUS→K, MINUS→I) es **decisión de andriuz29** y parece invertida para el
  cambio de marcha (en MyWhoosh `I`=subir, `K`=bajar). Lo importante es la **identidad del bit**; la
  tecla es configuración.
- Pulsaciones múltiples = varios bits en 0 a la vez (combinables de forma natural).

---

## 3. Otras fuentes revisadas

- **`ajchellew/zwiftplay`** (base de casi todo): decodificación del protocolo Zwift Play/Click,
  cifrado (HKDF/AES-CCM), tipos de mensaje. `CLICK_NOTIFICATION_MESSAGE_TYPE = 0x37` ("dos varints",
  era Play/Click V1). El Click V2 usa el tipo `0x23` de arriba.
- **`jat255/Zwift_click_handling`** (Python, reproduce a ajchellew): Click V1 de 2 botones,
  detecta plus/minus PRESSED/RELEASED, sin cifrado. Confirma CH02/CH03 y el handshake `RideOn`.
- **`OpenBikeControl/bikecontrol`** (Flutter, app madura): soporta Click v2, mapeo de botones
  configurable y "MyWhoosh link". Referencia de producto y de teclas MyWhoosh, no de bytes.

Fuentes: [andriuz29](https://github.com/andriuz29/Zwift-Click-V2-Universal-PC-Controller) ·
[ajchellew/zwiftplay](https://github.com/ajchellew/zwiftplay) ·
[jat255](https://github.com/jat255/Zwift_click_handling) ·
[OpenBikeControl/bikecontrol](https://github.com/OpenBikeControl/bikecontrol) ·
[Makinolo](https://www.makinolo.com/blog/2023/10/08/connecting-to-zwift-play-controllers/)

---

## 4. ⚠️ Discrepancia con lo que observó Violeta (a resolver)

Violeta documentó (en `ESTADO_ACTUAL.md`) firmas en CH02 que empiezan por **`08…`**
(`081064820`, `08001064180020`, keepalive `080010`), tratadas como opcode `0x08` (ZwiftPlayNotif).
Pero el mapa de la comunidad para el Click V2 son tramas **`2308…0F`** (tipo `0x23`).

Hipótesis de por qué no coinciden:
1. **Violeta hace el unlock server-backed completo**; `andriuz29` NO (solo `RideOn 02 03` +
   `000800`/`000810`). El stream post-unlock podría tener otro framing.
2. Violeta clasifica `data[0]==0x23` como **batería** (`BatteryStatus`) en `IsPlaintextZap` y lo
   manda a `DispatchPlaintext` → al log de estado, **no** al aprendiz de botones. Si el Click V2
   emite los botones como `0x23`, Violeta podría estar **ignorándolos como "batería"** y por eso la
   calibración por firma `0x08` era frágil.
3. La firma `08001064180020` de Violeta podría ser otra cosa (telemetría/estado), no el botón −.

**Esto es lo primero a verificar en hardware** en la próxima sesión.

---

## 5. Conclusión de diseño (recomendación fuerte)

Sustituir el enfoque de **calibración por firma + heurística de ráfaga** por un **decodificador
determinista del bitmask `0x23`**:

- Parsear `23 08 <varint bitmask> 0F`; comparar contra el estado anterior; cada **bit que pasa de
  1→0** es una **pulsación** de ese botón (y 0→1 es su release). Emitir una tecla por flanco de
  bajada.
- Ventajas: soporta los **10 botones** de fábrica, **multi-pulsación**, **cero calibración**, y
  elimina el problema del keepalive ambiguo (reposo = `FFFFFFFF`, ningún bit en 0 = nada que emitir).
- La calibración por firma queda como **respaldo** para hardware desconocido.
- El preset "MyWhoosh completo" mapea cada **bit/botón** → tecla MyWhoosh (I/K, ←/→, 1–7, U, H),
  configurable e invertible.

Mantener la regla de **ráfaga ≥5** solo como anti-rebote/anti-ruido encima del decodificador, no
como mecanismo principal de identificación.
