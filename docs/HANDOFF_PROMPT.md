# Prompt de continuación — Violeta / ZwiftClickV2-Bridge

> Pega esto como primer mensaje en una sesión nueva del agente. Resume el estado, lo aprendido y los
> próximos pasos concretos.

---

Estás retomando el desarrollo de **Violeta**, una app de escritorio Windows (WPF, .NET 8) que es la
cara amigable del **ZwiftClickV2-Bridge**: conecta un **Zwift Click V2** por BLE y traduce sus
botones a teclas para apps de ciclismo indoor (sobre todo **MyWhoosh**). Proyecto en
`D:\atahualpa-dev\scratch\ZwiftClickV2-Bridge`. Comentarios y textos de UI en español. Es software
libre (MIT), hecho por y para una ciclista; tono cuidado, identidad lila + guiño trans.

## Restricciones de entorno (importante)
- Es WPF `net8.0-windows`: **no compila en el sandbox Linux**. Escribe código cuidadoso; **la persona
  compila y prueba en Windows** con `dotnet run --project app`.
- El montaje bash (`/sessions/.../mnt/...`) puede ir **desfasado** respecto a las herramientas de
  archivo. Para verificar contenido real, usa **Read**, no `grep` por bash.
- La app **instalada** (`%LOCALAPPDATA%\Programs\Violeta\Violeta.exe`) es vieja; los cambios solo se
  ven con el build de desarrollo.

## Lo que ya se hizo en la sesión anterior
1. **Preferencias persistentes** (`app/Services/UserSettings.cs`): preset, inversión, mando, modo de
   acceso, ruta de MyWhoosh, y **mapa de botones aprendido** (`ButtonMap`, firma→acción) que persiste
   entre sesiones. Nunca guarda contraseña/token.
2. **Presets multi-botón** (`app/Models/KeyPreset.cs` + `ZwiftClickBridge.SetActions`): el puente
   mapea N acciones (no solo +/−). Presets: marchas, flechas, dirección, **MyWhoosh completo (10)**, emotes.
3. **Inicio = panel vivo** (`WelcomeView`): anillo de estado del mando (gris→latiendo→verde), CTA
   "Empezar a rodar", tiles, chips de botones. Estado compartido vía `app/Services/AppState.cs`.
4. **Conectar = asistente** (`ConnectView`): cabecera "Paso X de 6" + barra de progreso, jerarquía
   limpia, mostrar/ocultar contraseña, recordar correo, lanzador de MyWhoosh ("Empezar a rodar").
5. **Tema** reseñado (`app/Themes/Violeta.xaml`): tarjetas hero, tiles, chips, pulso.
6. **Emisión por RÁFAGA** (`ZwiftClickBridge`): un botón solo cuenta si su señal llega como **≥5
   tramas idénticas seguidas, sin mensaje intermedio** (`EmitBurstThreshold = 5`). Estándar para
   TODOS los botones. Resolvió el falso disparo del keepalive ambiguo.
7. **Perfil por defecto del Click V2** (`ClickV2Profile`, por prefijo) + **mapeo permanente**: los
   botones funcionan sin calibrar; la calibración quedó opcional (`AutoCalibrate=false`).

## ⚠️ HALLAZGO CLAVE (leer `docs/EXTERNAL_BUTTON_MAP_RESEARCH.md`)
La comunidad **ya decodificó los botones del Click V2 de forma determinista** (repo
`andriuz29/Zwift-Click-V2-Universal-PC-Controller`). No es por firma aprendida: es un **bitmask
activo-bajo** en una trama de tipo `0x23`:

```
23 08 <B0 B1 B2 B3> 0F     (reposo = 2308FFFFFFFF0F; cada bit en 0 = botón pulsado)
LEFT=B0&~0x01  UP=B0&~0x02  RIGHT=B0&~0x04  DOWN=B0&~0x08
PLUS=B1&~0x20  MINUS=B1&~0x02   (faltan ~4 botones del controlador derecho: bits por descubrir)
```

**Discrepancia a resolver:** Violeta venía observando tramas que empiezan por `08…` y trata `0x23`
como "batería" (`IsPlaintextZap`/`DispatchPlaintext`), así que **podría estar ignorando los botones
reales**. Verificar en hardware cuál framing emite el Click V2 tras el unlock server-backed.

## Próximos pasos (en orden)
1. **Capturar tramas crudas** del Click V2 de la persona (todos los canales/opcodes, sin filtrar) y
   compararlas con `2308…0F`. Confirmar si los botones llegan como tipo `0x23` (bitmask) o `0x08`.
   - Hay acceso a la pantalla (computer-use): se puede leer el registro 🔬 mientras pulsa cada botón.
2. Si se confirma el bitmask `0x23`: **implementar un decodificador determinista** en
   `ZwiftClickBridge` (parsear el varint, comparar con el estado previo, emitir en cada flanco 1→0).
   Sustituye a la calibración por firma como mecanismo principal; mantener ráfaga≥5 como anti-rebote
   y la calibración como respaldo para hardware desconocido.
3. **Mapear los 10 botones** a teclas MyWhoosh confirmadas (I/K, ←/→, 1–7, U, H) en el preset
   "MyWhoosh completo"; descubrir los ~4 bits que faltan pulsando esos botones.
4. Verificar de punta a punta en hardware: conectar → pulsar cada botón → tecla correcta, sin
   disparos en reposo. Actualizar `docs/BUTTON_MAP.md` y `docs/ESTADO_ACTUAL.md`.

## Mandatos de la persona (no olvidar)
- **Cero calibración**: que venga **lista para rodar** y configurada para MyWhoosh de fábrica.
- **Los 10 botones** del Click V2 deben mapearse (no solo +/−).
- **Ráfaga = estándar** para todos los botones (≥5 señales seguidas, sin interrupción).
- Respuestas **concisas y directas**; en español; sin sobreexplicar.

## Arquitectura (dónde tocar)
- `src/Bridge/ZwiftClickBridge.cs` — orquestador BLE→tecla, emisión por ráfaga, perfil/calibración.
- `src/Bridge/KeyboardEmulator.cs` — VK codes (incluye 1–7, U, H, flechas).
- `app/Models/KeyPreset.cs` — presets y acciones.
- `app/Services/` — `UserSettings`, `AppState`, `BridgeRunner`.
- `app/Views/` — `WelcomeView` (Inicio/panel), `ConnectView` (asistente), `TutorialView`, `AboutView`.
- Docs: `ESTADO_ACTUAL.md`, `BUTTON_MAP.md`, `EXTERNAL_BUTTON_MAP_RESEARCH.md` (este hallazgo).

Empieza confirmando el framing real de los botones en hardware antes de escribir el decodificador.
