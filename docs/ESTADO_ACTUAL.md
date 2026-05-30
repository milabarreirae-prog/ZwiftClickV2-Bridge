# Estado actual del proyecto — Violeta + ZwiftClickV2-Bridge

**Fecha:** 2026-05-30  
**Rama:** `main`  
**Autora:** Una mujer trans 🏳️‍⚧️

---

## ✅ Qué funciona completamente

### Conexión BLE
- Escaneo por advertising, match por nombre (contiene "Zwift").
- `GattSession` con `MaintainConnection=true` para mantener el enlace vivo.
- Descubrimiento de características **por UUID** en todos los servicios (primarios e incluidos): CH02, CH03, CH04, CH100/101/102.
- Emparejamiento Windows ("Just Works") como **fallback** automático si no aparecen las características.
- Suscripción reportada con éxito a CH02 (Notify), CH04 (Indicate), CH100/101/102 (Notify).

### Handshake ECDH
- `"RideOn 02 03" + localPubKey[64]` → CH03.
- El dispositivo emite el reto en **texto claro** en CH02: `FF 03 00 ‖ protobuf(82B)` con campos {pubkey, id, signature} generados por el propio mando.

### Unlock server-backed (validado en hardware)
- POST `/api/d-lock-service/device/authenticate` con los 82 bytes verbatim y `Bearer` del usuario → HTTP 204.
- Write `FF 04 00` → CH03 tras el 204.
- **No requiere token embebido**: usa OAuth de la cuenta del usuario (en memoria, nunca guardado).

### Stream de botones post-unlock
- Las notificaciones llegan **en texto claro** en CH02 con opcode `0x08` (`ZWIFT_PLAY_NOTIF`).
- Keepalive/reposo ~1/s: firma `080010` y variantes con ceros.
- Cada botón tiene su **firma estable**: hex de la trama sin ceros finales.
- Observado en hardware:
  - Botón derecha pad: `081064820…`
  - Botón arriba pad izquierdo: `081061820…`
  - Botón `−` pad izquierdo: `08001064180020…`
  - Release: `080010` (cierra toda ráfaga)

### Calibración por firma
- El bridge aprende las firmas de cada botón por calibración manual (5 s, "Calibrar +/−").
- `Signature(frame)` = hex sin ceros finales → identidad estable.
- El keepalive `080010` está pre-sembrado con baseline=999 (nunca elegido).
- Las firmas ya asignadas a otra acción se excluyen al calibrar la segunda.
- Al detectar la firma calibrada: emite **UNA** pulsación de tecla por ráfaga (en la transición de firma nueva), el release re-arma.

### Emulación de teclado
- `SendInput` (user32.dll): mapeo configurable `−` / `+` a cualquier tecla.
- Preset por defecto: **MyWoosh** (`I` = subir marcha, `K` = bajar).
- Otros presets: Flechas (`←`/`→`), Dirección (`A`/`D`).
- Checkbox "Invertir + y −".

### Interfaz gráfica Violeta
- WPF .NET 8, tema violeta/lila (inspirado en paleta MyWoosh, sin colores propietarios).
- Logo: ciclista con una flor en la cabeza.
- Vistas: Inicio · Cómo funciona (tutorial paso a paso) · Conectar mando · Acerca de.
- Pantalla Conectar: pasos en vivo iluminados en tiempo real, tarjeta Calibrar +/−, registro en consola, "Copiar registro".
- Mensaje claro: **gratis, libre y para siempre**. Hecha con cariño por una mujer trans 🏳️‍⚧️.
- Instalador: Inno Setup, por-usuario (sin admin), ES/EN, accesos directos.
- Icono generado: `app/Assets/violeta.ico` (emblema lila multiresolución).

---

## 🟡 Pendiente / conocido

### Calibración — caso ambiguo `08001064180020`
`08001064180020` aparece **tanto** como keepalive (~1/s) **como** ráfaga del botón `−` (~10 veces seguidas). El aprendiz de baseline lo clasifica correctamente después de ~3 observaciones en reposo. **Limitación:** si la calibración comienza en el primer segundo tras el unlock (antes de que el dispositivo haya emitido 3 keepalives), esa firma puede colarse como candidata a `+`. Mitigación práctica: espera 3-4 s tras el unlock antes de calibrar. Solución definitiva pendiente: detección de ráfaga por tasa (burst rate en la ventana de calibración).

### Cierre de la ventana de emparejamiento Windows
Windows muestra una ventana nativa de "Emparejar dispositivo" durante el intento de bonding (aunque el mando ya esté accesible sin él). La ventana aparece, falla con "Error de conexión", pero la app continúa con éxito. Es una regresión de UX menor: la próxima versión debería saltarse el intento de emparejamiento si las características ZAP ya son visibles sin él.

### Emulación de teclas en aplicaciones de foco especial
En algunas apps (juegos a pantalla completa con hooks propios), `SendInput` puede no llegar. Mitigación: las apps de ciclismo indoor conocidas (MyWoosh, BKOOL, etc.) funcionan.

### Descifrado de sesión post-unlock
El bake-off AES-256-CCM existe y está implementado, pero **no fue necesario**: el stream de botones llega en texto claro. Si en algún escenario futuro llegaran cifrados, el bake-off entraría en juego. Parámetros confirmados: AES-256-CCM, info vacío, salt device‖local, contador desde 0, AAD vacío.

### Opcode `0x08` — formato protobuf interno
El payload protobuf completo del `0x08` no está documentado externamente. Lo que sabemos: las primeras firmas observadas en hardware. El aprendiz de calibración hace que esto sea irrelevante para el uso práctico.

---

## 📦 Entregables

| Artefacto | Descripción |
|---|---|
| `dist/Violeta/Violeta.exe` | Ejecutable autocontenido (no requiere .NET instalado), ~80 MB |
| `dist/Violeta-Setup-1.0.0.exe` | Instalador con asistente en violeta, ~75 MB |
| `scripts/publish-app.ps1` | Genera el exe autocontenido |
| `scripts/build-installer.ps1` | Genera el instalador (requiere Inno Setup 6) |
| `scripts/sign-app.ps1` | Firma Authenticode (autofirmado o .pfx real) |
| `scripts/make-icon.ps1` | Regenera el .ico del logo |
| `docs/PACKAGING.md` | Guía completa de empaquetado, firma e instalador |

---

## 🔬 Hallazgos de investigación (primeros en el mundo para el Click V2)

1. **El reto llega en texto claro** en CH02 (`FF 03 00` + protobuf): confirmado en hardware, contradice la hipótesis previa de que era cifrado.
2. **Los botones llegan en texto claro** en CH02 con opcode `0x08` (no `0x38` como decían todos los docs de V1/V2). Confirmado con hardware real.
3. **El servicio ZAP `00000001-19CA…` no aparece como primario** en Windows hasta después del bonding; las características se encuentran enumerando todos los servicios.
4. **El unlock NO requiere ningún comando de activación** posterior al `FF 04 00`. El stream de botones fluye solo.
5. **El keepalive `080010`** se emite ~1/s y cierra cada ráfaga de botón.

---

## 🎯 Próximos pasos sugeridos

1. **Calibración automática**: tras observar N segundos de reposo, aprender el baseline sin intervención del usuario y eliminar la necesidad de calibración manual.
2. **Detección de burst rate**: para distinguir el caso ambiguo `08001064180020` (keepalive vs botón −) por tasa en vez de por conteo acumulado.
3. **Saltar emparejamiento innecesario**: no llamar a `EnsurePairedAsync` si `FindZapCharacteristicsAsync` ya tuvo éxito en el primer intento.
4. **Firma de código**: comprar un certificado OV/EV para eliminar el aviso de SmartScreen.
5. **Documentar el protobuf `0x08`**: con más capturas de los distintos botones del Click V2, reconstruir el esquema completo.
