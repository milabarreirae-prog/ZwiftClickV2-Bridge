# Guía de Contribución

¡Gracias por tu interés en contribuir a **ZwiftClickV2-Bridge**!

## 🎯 Áreas donde Necesitamos Ayuda

### 1. Ingeniería Inversa del Handshake V2

**Problema:** El handshake V2 usa un header de 7 bytes cuyo contenido exacto es desconocido. El formato completo es `[Header 7 bytes] + [0x04] + [EC Public Key 64 bytes]` (72 bytes total).

**Hipótesis probadas (todas rechazadas):**
- `"RideOn" + 0x00` a `"RideOn" + 0x04`
- `AllZeros`, `AllFF`, `Sequence1-7`, `Version1`, `Version2`
- Handshakes basados en "RideOn" + sufijos varios (ver `--probe` y `--fuzz`)

**Cómo ayudar:**
- Capturar tráfico BLE entre **Zwift oficial** y **Click V2** usando un sniffer hardware y compartir los logs
- Analizar los primeros 72 bytes del handshake enviado por ZwiftApp.exe
- Probar headers adicionales modificando `HEADERS_TO_TEST` en `src/Protocol/HandshakeFuzzerV2.cs`

**Herramientas recomendadas:**
| Dispositivo | Precio aprox. | Setup |
|---|---|---|
| [nRF52840 Dongle](https://www.nordicsemi.com/Products/Development-hardware/nrf52840-dongle) | ~$15 USD | + [nRF Sniffer for Wireshark](https://www.nordicsemi.com/Products/Development-tools/nrf-sniffer-for-wireshark) |
| [Ubertooth One](https://greatscottgadgets.com/ubertoothone/) | ~$120 USD | + Wireshark BLE plugin |
| [Adafruit Bluefruit LE Sniffer](https://www.adafruit.com/product/2269) | ~$30 USD | + Wireshark |

### 2. Protocolo ZOP (Zwift Operations Protocol)

**Problema:** Los mensajes ZOP después del handshake están cifrados con AES-128-GCM y no conocemos su estructura exacta.

**Cómo ayudar:**
- Si tienes acceso al APK/IPA de Zwift Companion, decompilar y extraer los schemas Protobuf
- Buscar definiciones de `ControllerNotification`, `Hello`, `Welcome`, `CapabilityRequest`, etc.
- Extraer FileDescriptorProto de la sección `.rdata` de ZwiftApp.exe (Windows/Linux/macOS)

### 3. Testing con Dispositivos Reales

**Cómo ayudar:**
- Ejecutar los fuzzers (`--probe`, `--fuzz`, `--fuzz-v2`) y compartir los logs JSON
- Probar el bridge (`--bridge`) con un Zwift Play V1 funcional
- Reportar códigos de error observados en las respuestas del dispositivo

## 📝 Cómo Enviar Contribuciones

1. **Fork** el repositorio: https://github.com/milabarreirae-prog/ZwiftClickV2-Bridge
2. Crea una rama para tu feature:
   ```bash
   git checkout -b feature/nombre-de-la-funcionalidad
   ```
3. Haz commit de tus cambios:
   ```bash
   git commit -m 'Descripción clara del cambio'
   ```
4. Push a tu fork:
   ```bash
   git push origin feature/nombre-de-la-funcionalidad
   ```
5. Abre un **Pull Request** describiendo el cambio y por qué es necesario

### Estándares de Código

- Usa C# 12 con .NET 8 (`net8.0-windows10.0.19041.0`)
- Namespace: `ZwiftClickV2.Bridge.*`
- Nombres de clases en PascalCase, métodos en PascalCase
- Documenta métodos públicos con `<summary>` XML comments
- Tests unitarios en `tests/` con xUnit

## 🐛 Reportar Bugs

Usa [GitHub Issues](https://github.com/milabarreirae-prog/ZwiftClickV2-Bridge/issues) con la siguiente información:

- Versión del dispositivo (**V1** Play 2023 o **V2** Click 2025)
- Logs completos de la ejecución (archivo JSON generado en `logs/`)
- Sistema operativo y versión de .NET
- Pasos exactos para reproducir el problema
- Salida completa de la consola

## 💡 Ideas y Sugerencias

¿Tienes una idea para mejorar el proyecto? Abre un [GitHub Issue](https://github.com/milabarreirae-prog/ZwiftClickV2-Bridge/issues) con la etiqueta `enhancement`.

## 📄 Licencia

Al contribuir, aceptas que tus contribuciones se licencien bajo la **MIT License** del proyecto.

---

**Proyecto mantenido por [milabarreirae-prog](https://github.com/milabarreirae-prog)**