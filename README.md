# ZwiftClickV2-Bridge

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Status](https://img.shields.io/badge/Status-In%20Development-orange)]()

Bridge open-source para conectar **Zwift Click V2** (modelo 2025) a **MyWoosh** y otras plataformas de ciclismo indoor vía emulación de teclado.

## 📊 Estado Actual

| Componente | Estado | Descripción |
|---|---|---|
| **Conexión BLE** | ✅ Funcional | Escaneo, conexión, suscripción a notificaciones |
| **Handshake V1** | ✅ Funcional | Zwift Play 2023 (modelo anterior) |
| **Handshake V2** | 🔬 En investigación | Click 2025 usa `RideOn 01 02` y espera respuesta `RideOn 01 03` |
| **Criptografía** | ✅ Implementado | ECDH P-256 + HKDF 128B + AES-256-CCM |
| **Emulación de teclado** | ✅ Funcional | `SendInput` API para MyWoosh/otras apps |

## 🔬 Investigación en Curso

Estamos realizando ingeniería inversa del protocolo **Zwift Click V2**. Hallazgos clave documentados en [REVERSE_ENGINEERING_V2.md](docs/REVERSE_ENGINEERING_V2.md):

- **Esquema criptográfico:** ECDH P-256 → HKDF (`info="handshake data"`, salt 128B) → AES-256-CCM (tag 4B)
- **Formato de handshake V2:** `RideOn 01 02 + pubkey[64]` y respuesta `RideOn 01 03 + pubkey[64]`
- **Problema actual:** El header de 7 bytes es desconocido. El dispositivo rechaza handshakes con códigos de error Protobuf (`58 02`, `C0 03`)
- **Device auth:** el binario muestra `auth challenge` resuelto vía `INetworkService` con `Authorization: Bearer <access_token>`

### Fuzzers Disponibles

| Comando | Qué prueba | Variaciones |
|---|---|---|
| `--probe` | Formatos V1 + V2 básicos | 6 formatos |
| `--fuzz` | Variaciones de "RideOn" + sufijos + claves | ~15 variaciones |
| `--fuzz-v2` | Headers de 7 bytes del handshake V2 | 10 headers |

## 🚀 Uso Rápido

### Requisitos

- **Windows 10/11** (2004+)
- **.NET 8.0 SDK**
- **Zwift Click V2** (o Zwift Play V1 para probar)

### Instalación

```bash
git clone https://github.com/milabarreirae-prog/ZwiftClickV2-Bridge.git
cd ZwiftClickV2-Bridge
dotnet build
```

### Ejecutar Fuzzers

```bash
# Fuzzer V2: probar headers de 7 bytes
dotnet run --project src -- --fuzz-v2

# Prober básico: 6 formatos de handshake
dotnet run --project src -- --probe

# Fuzzer exhaustivo: ~15 variaciones con logs JSON
dotnet run --project src -- --fuzz
```

### Ejecutar Bridge y diagnóstico V2

```bash
# Hot pair estándar
dotnet run --project src -- --hot-pair

# Hot pair + diagnóstico cripto V2
dotnet run --project src -- --diagnose-crypto
```

Los botones del dispositivo se emulan como teclas de teclado:
- **Click izquierdo** → `VK_LEFT` (flecha izquierda)
- **Click derecho** → `VK_RIGHT` (flecha derecha)

## 📚 Documentación

| Documento | Descripción |
|---|---|
| [REVERSE_ENGINEERING_V2.md](docs/REVERSE_ENGINEERING_V2.md) | Análisis completo del protocolo: criptografía, handshake, BLE, ZOP, DRM |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Guía para contribuir al proyecto |
| [REVERSE_ENGINEERING.md](docs/REVERSE_ENGINEERING.md) | Documentación original (handshake V1) |

## 🛠️ Desarrollo

### Estructura del Proyecto

```
ZwiftClickV2-Bridge/
├── src/
│   ├── BLE/              # Conexión y comunicación BLE
│   ├── Crypto/           # ECDH, HKDF, AES-256-CCM
│   ├── Protocol/         # Handshake, ZOP, fuzzers
│   ├── Bridge/           # Orquestador, emulación de teclado
│   └── Logging/          # Logger JSON estructurado
├── tests/                # Tests unitarios (xUnit)
└── docs/                 # Documentación de ingeniería inversa
```

### Compilar

```bash
dotnet publish -c Release -o bin/Release
```

### Tests

```bash
dotnet test
```

## 🤝 Cómo Contribuir

¡Necesitamos tu ayuda! Ver [CONTRIBUTING.md](CONTRIBUTING.md) para detalles.

**Áreas críticas:**
1. **Sniffer BLE:** Capturar tráfico entre Zwift oficial y Click V2
2. **Ingeniería inversa:** Analizar ZwiftApp.exe con Ghidra
3. **Testing:** Ejecutar fuzzers con dispositivos reales

**Herramientas útiles:**
- [nRF52840 Dongle](https://www.nordicsemi.com/Products/Development-hardware/nrf52840-dongle) (~$15 USD) — Sniffer BLE económico
- [Ghidra](https://ghidra-sre.org/) — Decompilador para analizar ZwiftApp.exe
- [Wireshark](https://www.wireshark.org/) + nRF Sniffer plugin

## 🙏 Agradecimientos

- **Makinolo** — [Documentación del protocolo Zwift Play V1](https://www.makinolo.com/blog/2023/10/08/connecting-to-zwift-play-controllers/)
- **ajchellew/zwiftplay** — [Implementación Python de referencia](https://github.com/ajchellew/zwiftplay)
- **QDomyos-Zwift** — [Inspiración y comunidad](https://github.com/cagnulein/qdomyos-zwift)

## 📄 Licencia

MIT License — Ver [LICENSE](LICENSE) para detalles.

---

*Este proyecto no está afiliado oficialmente con Zwift, Inc. Zwift y Zwift Play son marcas registradas de Zwift, Inc.*