# Guía de Contribución

¡Gracias por tu interés en contribuir a **ZwiftClickV2-Bridge**!

El protocolo ZAP del Click V2 ya está mayormente resuelto (ver [docs/protocol/](docs/protocol/README.md)).
El proyecto está en fase research/alpha y la ayuda más valiosa hoy es cerrar la última incógnita y
validar contra hardware real.

## 🎯 Áreas donde necesitamos ayuda

### 1. La incógnita que bloquea el unlock de extremo a extremo

El request `POST /api/d-lock-service/device/authenticate` lleva un protobuf
`{1: pubkey_comprimida, 2: id, 3: firma(40B)}`. El campo 1 sale del handshake BLE; **el origen de
los campos 2 (id) y 3 (firma) — que emite el dispositivo por BLE — aún no está resuelto.**

Cómo ayudar:
- Capturar el handshake BLE completo (con sus fragmentos de continuación) y las notificaciones de
  CH100/101/102 durante el emparejamiento con la app oficial, y correlacionarlas con el body HTTP.
- Localizar en la ruta de auth del firmware/app dónde se generan/leen esos dos campos.
- Una vez identificado, completar `ZwiftClickBridge.TryAssembleChallenge`.

### 2. Validación cripto contra un Click V2 real

- Confirmar `HkdfInfoMode` (vacío vs `"handshake data"`): probar `--bridge` y `--bridge --legacy-hkdf-info`.
- Confirmar AES-256-CCM y la derivación ECDH raw con un round-trip de descifrado real en CH02.

### 3. Mapeo de eventos de botón

- Confirmar la estructura del opcode `0x38` (ZWIFT_CLICK_NOTIFICATION) en una sesión desbloqueada y
  ajustar `ZopPeripheralEvent` (los nombres actuales son provisionales, no asumir formato de Play).

## 📝 Cómo enviar contribuciones

1. Haz **fork** del repositorio.
2. Crea una rama: `git checkout -b feature/mi-cambio`.
3. Commit y push a tu fork.
4. Abre un **Pull Request** describiendo el cambio y por qué es necesario.

### Estándares de código

- C# 12 / .NET 8 (`net8.0-windows10.0.19041.0`), namespace `ZwiftClickV2.Bridge.*`.
- PascalCase para clases/métodos; `_camelCase` privados; async `...Async`.
- Documenta lo público con XML `<summary>`. Tests en `tests/` con xUnit (`[Fact]`).
- `dotnet build` y `dotnet test` deben pasar antes del PR.

### 🔒 Nunca incluyas en un PR

- Tokens OAuth, credenciales, ni capturas crudas (`out/mitm/`, `phaseC-captures/`).
- El decompile `x.c` ni binarios de Zwift (propietarios).
- Datos identificables de tu dispositivo (pubkey, serial, id) sin redactar.

## 🐛 Reportar bugs

Abre un GitHub Issue con: versión del dispositivo (Play V1 / Click V2), logs de `logs/*.json`
(revisa que no contengan datos sensibles), SO + versión de .NET, y pasos para reproducir.

## 📄 Licencia

Al contribuir, aceptas que tus contribuciones se licencien bajo la **MIT License** del proyecto.
