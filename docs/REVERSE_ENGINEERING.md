# Documentación de Ingeniería Inversa — Zwift Click V2

## Descripción General

Este documento recopila los hallazgos de la ingeniería inversa del protocolo de comunicación
BLE entre el dispositivo **Zwift Click V2** (2025) y la aplicación **Zwift** en Windows.
Incluye también el protocolo **Zwift Play 2023** (V1).

---

## Estructura del Protocolo

### Servicios y Características BLE

| Servicio UUID | Característica UUID | Tipo | Nombre | Descripción |
|--------------|-------------------|------|--------|-------------|
| `0000fc82-0000-1000-8000-00805f9b34fb` | `00000002-...d09d1` | Notify | CH02 | Eventos de botones (cifrados) |
| | `00000003-...d09d1` | Write | CH03 / SyncTx | Handshake inicial + writes ZOP |
| | `00000004-...d09d1` | Indicate | CH04 / SyncRx | Respuesta Welcome + notificaciones |
| | `00000006-...d09d1` | Read | CH06 | Información del dispositivo |
| | `00000100-...d09d1` | ? | CH100 | ? |
| | `00000101-...d09d1` | ? | CH101 | ? |
| | `00000102-...d09d1` | ? | CH102 | ? |

### Flujo de Comunicación

```
1. BLE Connection
   └── Escaneo y conexión al dispositivo "Zwift Click"

2. Handshake (SyncTx/SyncRx)
   ├── Bridge → SyncTx: "RideOn" + sufijo + pubkey[65]
   │   Sufijos probados: 01 01, 01 02, 02 03, 00 09
   └── SyncRx → Bridge: "RideOn" + sufijo + datos

3. Detección de Versión
   ├── V1 (Play 2023): sufijos 01 01, 00 09 → AES-128-CCM
   └── V2 (Click 2025): sufijos 02 03, 01 02 → AES-128-GCM

4. Establecimiento de Cifrado
   ├── ECDH P-256: derivar shared secret (32B)
   ├── HKDF-SHA256:
   │   ├── V1: salt = 32B ceros, info = vacío
   │   └── V2: salt = peer_pub[1:65] || SHA256(peer_pub[1:65]) = 96B, info = vacío
   ├── Output: 36 bytes
   │   ├── derived[0:16]  = AES-128 key
   │   └── derived[32:36] = nonce (4B)
   └── IV = nonce[4] || counter[4] BE = 8 bytes

5. Post-Handshake (ZOP writes cifrados)
   ├── Write 1: Capability
   ├── Write 2: Ping
   ├── Write 3: Empty
   └── Write 4: Ping

6. Operación Normal
   ├── CH02 → Bridge: PeripheralEvent (cifrado)
   │   ├── seq LE 4B + ciphered_payload
   │   └── Eventos: LeftClick, RightClick, Hold, Release
   ├── Keep-alive: Ping cada 5s en CH03
   └── Bridge → MyWoosh: SendInput (VK_LEFT / VK_RIGHT)
```

### Formato de Wire

```
Handshake inicial:
  "RideOn" [6B] + sufijo [2B] + pubkey [65B] = 73B

Mensajes ZOP (post-handshake):
  [seq:4B LE] + [ciphered_payload + tag:4B]
```

---

## Criptografía

### V2 — Click 2025 (AES-128-GCM)

| Componente | Algoritmo | Detalles |
|-----------|----------|----------|
| Intercambio | ECDH | P-256 (secp256r1) |
| Derivación | HKDF-SHA256 | Salt = peer_pub[64] \|\| SHA256(peer_pub[64]) = 96B |
| Info | NULL | Array vacío |
| Output | 36B | key[0:16], nonce[32:36] |
| Cifrado | AES-128-GCM | IV = nonce[4] + counter[4] BE = 8B |
| Tag | 4 bytes | Truncado de los 16B de GCM |
| Counter | uint32 BE | Empieza en 0, incrementa por mensaje |

### V1 — Play 2023 (AES-128-CCM)

| Componente | Algoritmo | Detalles |
|-----------|----------|----------|
| Intercambio | ECDH | P-256 |
| Derivación | HKDF-SHA256 | Salt = 32B ceros, info = vacío |
| Cifrado | AES-128-CCM | IV = nonce[4] + counter[4] BE = 8B |
| Tag | 4 bytes | |

---

## Mensajes ZOP

### Hello (Bridge → Dispositivo)
```
"RideOn" [6B] + sufijo [2B] + pubkey_ecdh [65B]
```

### Welcome (Dispositivo → Bridge)
```
"RideOn" [6B] + sufijo [2B] + pubkey_ecdh [65B] (+ datos adicionales)
```

Sufijos conocidos:
- `01 01` — V1 handshake accept
- `01 02` — V1/V2 handshake
- `02 03` — V2 status/event response
- `00 09` — V1 alternative

### Capability (post-handshake)
Payload Protobuf (pendiente de definir exactamente).

### Ping (keep-alive)
Payload vacío, enviado cada ~5 segundos.

### PeripheralEvent (CH02)
Eventos de botones descifrados:
- `01` = LeftClick
- `02` = RightClick
- `03` = LeftHold
- `04` = RightHold
- `05` = ButtonRelease

---

## Implementación (.NET 8)

### Estructura del Proyecto

```
ZwiftClickV2-Bridge/
├── src/
│   ├── BLE/
│   │   ├── BleDeviceManager.cs          # Escaneo, conexión, lectura CH06
│   │   ├── BleCharacteristicWriter.cs   # Escritura [seq LE] + payload
│   │   └── BleNotificationListener.cs   # Suscripción multi-característica
│   ├── Crypto/
│   │   ├── ZPEncryptionV1.cs            # V1: AES-128-CCM
│   │   ├── ZPEncryptionV2.cs            # V2: AES-128-GCM + salt 96B
│   │   └── ZPEncryptionFactory.cs       # Detección V1 vs V2 por sufijo
│   ├── Protocol/
│   │   ├── ZopSequencer.cs              # Sequence LE + encrypt/decrypt
│   │   ├── HandshakeParser.cs           # Parseo de respuestas con sufijos
│   │   ├── ZopMessage.cs                # Tipos de mensaje ZOP
│   │   ├── ZopSerializer.cs             # Serialización Protobuf
│   │   ├── ZwiftClickProtocol.cs        # Protocolo específico
│   │   ├── HandshakeProber.cs           # Probing de 6 formatos
│   │   └── Messages/
│   │       ├── ZopHello.cs
│   │       ├── ZopWelcome.cs
│   │       ├── ZopCapability.cs
│   │       ├── ZopPing.cs
│   │       └── ZopPeripheralEvent.cs
│   └── Bridge/
│       ├── ClickV2Bridge.cs             # Orquestador principal (8 pasos)
│       ├── KeyboardEmulator.cs          # SendInput user32.dll
│       └── MysteryPacketAnalyzer.cs     # Análisis de paquetes no descifrables
├── tests/
│   └── ZPEncryptionTests.cs             # 11 tests (V1, V2, Factory)
└── docs/
    └── REVERSE_ENGINEERING.md           # Este documento
```

### Ejecución

```bash
# Bridge completo
ZwiftClickV2-Bridge.exe --bridge

# Prober de handshake
ZwiftClickV2-Bridge.exe --probe
```

### Dependencias

- .NET 8 (`net8.0-windows10.0.19041.0`)
- `Google.Protobuf` 3.35.0
- `Portable.BouncyCastle` 1.9.0 (para GCM/CCM con tag 4B)
- `Microsoft.Windows.SDK.NET.Ref` (implícito, para WinRT BLE)

---

## Notas de Implementación

- El target `net8.0-windows10.0.19041.0` es necesario para `Windows.Devices.Bluetooth`
- La aplicación solo funciona en **Windows 10 2004+**
- `AesGcm` de .NET no soporta tags de 4 bytes → se usa BouncyCastle
- El salt HKDF V2 usa SIEMPRE la clave del **dispositivo**, no la del bridge
- Ambos lados deben usar el mismo salt para derivar la misma clave
- La emulación de teclado usa `SendInput` de `user32.dll`

## Recursos

- [Bluetooth LE en Windows](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/bluetooth-low-energy-overview)
- [ECDiffieHellman (.NET)](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecdiffiehellman)
- [AesGcm (.NET 8)](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm)
- [BouncyCastle GCM](https://github.com/bcgit/bc-csharp)
- [HKDF RFC 5869](https://datatracker.ietf.org/doc/html/rfc5869)
- [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)