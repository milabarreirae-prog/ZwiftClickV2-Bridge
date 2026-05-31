# Mapa de botones del Click V2 → teclas (presets de Violeta)

## ✅ Modo V1/andriuz — los 10 botones, confirmados en hardware (2026-05-30)

**Este es el modo por defecto.** El Zwift Click V2 es compatible con el protocolo del Click V1: tras un
`RideOn 02 03` corto (8B, sin pubkey) + las escrituras `000800` y `000810` en CH03, el mando emite los
botones **EN CLARO** por CH02 como un **bitmask activo-bajo**, **sin cuenta, sin cifrado, sin unlock y
sin calibración**:

```
23 08 B0 B1 B2 B3 0F     (reposo = 2308 FFFFFFFF 0F; cada bit en 0 = botón pulsado)
```

Mapa **verificado pulsando cada botón** (`ZwiftClickBridge.AndriuzButtons`):

| Botón físico | Byte/bit | Acción (id) | Tecla MyWhoosh |
|---|---|---|---|
| ← (izq.)       | B0 `0x01` | `left`     | ← |
| ↑ (arriba)     | B0 `0x02` | `nav_up`   | U |
| → (der.)       | B0 `0x04` | `right`    | → |
| ↓ (abajo)      | B0 `0x08` | `nav_down` | H |
| **b** (der.)   | B0 `0x10` | `btn_b`    | 2 |
| **a** (der.)   | B0 `0x20` | `btn_a`    | 1 |
| **y** (der.)   | B0 `0x40` | `btn_x`    | 3 |
| **z** (der.)   | B1 `0x01` | `btn_y`    | 4 |
| + (subir)      | B1 `0x02` | `plus`     | I |
| − (bajar)      | B1 `0x20` | `minus`    | K |

El decodificador (`DecodeButtonBitmask`) emite una tecla por cada bit que pasa de **1→0** (flanco de
pulsación). El modo V2 (handshake ECDH + unlock con cuenta) se conserva como alterno, pero **solo da
telemetría cifrada, no botones** (ver `docs/EXTERNAL_BUTTON_MAP_RESEARCH.md`).

> Cómo se confirmó: `dotnet run --project src -- --andriuz "Zwift Click"` (modo headless de diagnóstico)
> mientras se pulsaba cada botón; el orden de aparición en `logs/session_*.json` fijó el mapa a/b/y/z.

---

## Listo para rodar — sin calibrar (v1.2, modo V2 — histórico)

El Click V2 **viene precalibrado**: el puente trae un perfil por defecto con las firmas reales del
mando (`ClickV2Profile` en `ZwiftClickBridge`), así que al conectar los botones `+` y `−` ya envían
`I`/`K` (marchas de MyWhoosh) sin que el usuario calibre nada.

Dos piezas lo hacen posible y robusto:

1. **Perfil por defecto por prefijo.** Las firmas observadas en hardware (`08001064180020` → `−`,
   `0810…` → `+`) se reconocen por prefijo. La calibración del usuario, si la hay, tiene prioridad.
2. **Emisión por ráfaga.** La firma del botón `−` coincide con un keepalive de reposo. Para no
   disparar la tecla en reposo, el puente solo emite cuando la firma llega como **ráfaga** (≥
   `EmitBurstThreshold` tramas seguidas = una pulsación), nunca como latido aislado.

La **calibración es opcional**: solo se usa si un botón no responde o si el mando no es un Click V2.

---

Violeta mapea los botones del mando a teclas con un **perfil por defecto** (arriba) y, como respaldo,
un **aprendiz de firma por calibración**. Los presets de la interfaz definen **qué tecla** envía cada
acción.

## Presets disponibles (app/Models/KeyPreset.cs)

| Preset | Acción (`id`) | Símbolo | Tecla | Función en MyWhoosh |
|---|---|---|---|---|
| **Cambio de marchas** | `plus` | `+` | `I` | Subir marcha |
| | `minus` | `−` | `K` | Bajar marcha |
| **Flechas** | `plus` | `+` | `→` | Derecha |
| | `minus` | `−` | `←` | Izquierda |
| **Dirección** | `plus` | `+` | `D` | Girar derecha |
| | `minus` | `−` | `A` | Girar izquierda |
| **MyWhoosh completo** (10 botones) | `plus` | `+` | `I` | Subir marcha |
| | `minus` | `−` | `K` | Bajar marcha |
| | `left` | `←` | `←` | Girar izquierda |
| | `right` | `→` | `→` | Girar derecha |
| | `nav_up` | `↑` | `U` | Alternar UI mínima |
| | `nav_down` | `↓` | `H` | Ocultar UI (solo HD) |
| | `btn_a` | `A` | `1` | Emote: paz |
| | `btn_b` | `B` | `2` | Emote: saludo |
| | `btn_x` | `X` | `3` | Emote: choque de puños |
| | `btn_y` | `Y` | `4` | Emote: dab |
| **Emotes** | `plus`/`minus`/`btn_x`/`btn_y` | — | `1`–`4` | Paz / saludo / choque / dab |

> Solo se mapean atajos **confirmados** de MyWhoosh (julio 2025): marchas `I`/`K`, emotes `1`–`7`,
> dirección `←`/`→` (y `A`/`D`), UI `U`, ocultar `H`. Cámara, intensidad % y ERG on/off son
> **peticiones de la comunidad aún no implementadas** por MyWhoosh, así que no se pueden mapear todavía.

### Calibración por ráfaga (firmas ambiguas)

Algunas firmas (p. ej. `08001064180020`) aparecen **a la vez** como keepalive (~1/s) y como ráfaga
de un botón (~10 por pulsación). La calibración ya no descarta una firma solo por verse en reposo:
si una firma también aparece en reposo, exige superar un **umbral de ráfaga** (`CalBurstThreshold`)
dentro de la ventana para aceptarla como botón. Por eso conviene apretar el botón **firme y varias
veces** cuando se calibra. Esto arregla el caso en que el botón `−` no se detectaba.

El conmutador **«Invertir + y −»** intercambia las teclas de `plus` y `minus` (por si el hardware
los reporta al revés). El resto de acciones no se ven afectadas.

## Cómo añadir o cambiar un mapeo

1. Edita la lista `KeyPresets.All` en `app/Models/KeyPreset.cs`.
2. Cada `MappableAction` necesita un `Id` estable, un `Glyph` (símbolo para la UI), un `Label`
   humano y una `Key` (constante de `KeyboardEmulator`, p. ej. `VK_I`, `VK_1`, `VK_LEFT`).
3. La interfaz construye automáticamente una **ficha de calibración por cada acción** del preset, y
   la **calibración automática guiada** las recorre en orden.

## Firmas observadas en hardware (Click V2)

Capturadas en el registro 🔬 de la pantalla **Conectar** (opcode `0x08` en CH02, en claro):

| Firma (hex sin ceros finales) | Botón |
|---|---|
| `081064820…` | Pad derecho |
| `081061820…` | Pad izquierdo, arriba |
| `08001064180020…` | `−` pad izquierdo (también aparece como keepalive) |
| `080010` | Reposo / keepalive (se ignora) |

> Estas firmas dependen del firmware del mando. Por eso Violeta **aprende** la firma por
> calibración en lugar de fijarla por código: funciona aunque cambien entre unidades o versiones.
> Cuando el protobuf del `0x08` esté documentado del todo, el aprendiz podrá sustituirse por un
> parser determinista (pendiente #8 en `ESTADO_ACTUAL.md`).
