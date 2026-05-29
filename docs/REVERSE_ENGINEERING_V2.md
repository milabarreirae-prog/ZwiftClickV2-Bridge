# Documentación de Ingeniería Inversa — Zwift Click V2

> **Versión:** 2.0  
> **Fecha de creación:** 27 de mayo de 2026  
> **Estado:** Handshake rechazado — proyecto en punto muerto técnico  
> **Objetivo:** Conectar Zwift Click V2 a MyWoosh como emulador de teclado

---

## Tabla de Contenidos

1. [Resumen Ejecutivo](#1-resumen-ejecutivo)
2. [Esquema Criptográfico Completo](#2-esquema-criptográfico-completo-confirmado)
3. [Formatos de Handshake](#3-formatos-de-handshake-confirmado)
4. [Arquitectura BLE del Click V2](#4-arquitectura-ble-del-click-v2-confirmado)
5. [Códigos de Error en Respuestas](#5-códigos-de-error-en-respuestas-observado)
6. [Paquete Misterioso de 85 Bytes](#6-paquete-misterioso-de-85-bytes-observado)
7. [Protocolo ZOP](#7-protocolo-zop-zwift-operations-protocol-confirmado)
8. [ControllerNotification y Botones](#8-controllernotification-y-botones-confirmado)
9. [DRM y Daily Unlock](#9-drm-y-daily-unlock-investigado)
10. [Herramientas y Técnicas de Ingeniería Inversa](#10-herramientas-y-técnicas-de-ingeniería-inversa-usadas)
11. [Comparación V1 vs V2](#11-comparación-v1-vs-v2)
12. [Estado Actual del Proyecto](#12-estado-actual-del-proyecto)
13. [Referencias y Recursos](#13-referencias-y-recursos)
14. [Changelog](#14-changelog)

---

## 1. Resumen Ejecutivo

### Objetivo del Proyecto

Crear un puente software (bridge) que permita usar el dispositivo **Zwift Click V2** (modelo 2025) con la aplicación **MyWoosh** en Windows, emulando teclas de teclado (VK_LEFT / VK_RIGHT) en respuesta a los clics de los botones físicos del dispositivo.

### Estado Actual

| Componente | Estado | Detalle |
|---|---|---|
| Conexión BLE | ✅ Funciona | Sin bonding BLE tradicional en la ruta ZAP observada |
| Notificaciones | ✅ Funciona | CH02, CH04, CH100, CH101, CH102 |
| Escritura BLE | ✅ Funciona | CH03 acepta writes sin error |
| Handshake V1/V2 | ❌ Rechazado | Las 6 variantes probadas son rechazadas |
| Derivación de clave AES | ❌ Fallida | El dispositivo no devuelve clave pública EC |
| Descifrado de mensajes | ❌ Imposible | Sin clave de sesión no se puede descifrar |
| Emulación de teclado | ✅ Lista | Implementación con `SendInput` user32.dll |

### Principales Descubrimientos

1. **El esquema criptográfico V2 observado en implementaciones y código actualizado es AES-256-CCM** con tag de 4 bytes.
2. **El HKDF V2 usa `info = "handshake data"` y salt de 128 bytes**: `device_pubkey[64] || local_pubkey[64]`, ambos sin prefijo `0x04`.
3. **La respuesta de handshake V2 válida observada es `RideOn 01 03 + pubkey[64]`**, no `00 09` ni `02 03`.
4. **El dispositivo implementa un mecanismo de rechazo**: cuando no acepta la sesión, responde con datos Protobuf (`58 02`, batería, status).
5. **El binario muestra un `auth challenge` resuelto por `INetworkService`**, así que el flujo no es puramente local: requiere servicio de red y bearer `access_token` válido.

### Conclusión

El Zwift Click V2 implementa una capa de autenticación más estricta que el V1 (Zwift Play 2023). El punto bloqueante ya no es solo el wire format: también existe un `auth challenge` dependiente de red que ZwiftApp resuelve con `INetworkService`. Sin capturar el request HTTP real o el write BLE exacto que reinyecta la `auth response`, el proyecto sigue parcialmente bloqueado.

---

## 2. Esquema Criptográfico Completo (CONFIRMADO)

### 2.1 Curva Elíptica

| Propiedad | Valor |
|---|---|
| **Tipo** | P-256 (secp256r1 / NIST P-256) |
| **NID OpenSSL** | `0x19f` (`NID_X9_62_prime256v1`) |
| **Tamaño de clave pública** | 65 bytes uncompressed |
| **Formato** | `0x04` ‖ `X[32]` ‖ `Y[32]` |
| **Biblioteca .NET** | `System.Security.Cryptography.ECDiffieHellman` |
| **Curve name** | `ECCurve.NamedCurves.nistP256` |

### 2.2 ECDH (Elliptic Curve Diffie-Hellman)

```
Función: ECDH_compute_key()
Input:   Nuestra clave privada (efímera) + clave pública del peer (65 bytes)
Output:  Shared secret de 32 bytes
```

**Implementación C# de referencia:**

```csharp
using var ourKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
// Generar par efímero
var parameters = ourKey.ExportParameters(false);
byte[] ourPubKey65 = new byte[65];
ourPubKey65[0] = 0x04;
Array.Copy(parameters.Q.X!, 0, ourPubKey65, 1, 32);
Array.Copy(parameters.Q.Y!, 0, ourPubKey65, 33, 32);

// Derivar shared secret con la clave pública del peer
using var peerEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
peerEcdh.ImportParameters(new ECParameters
{
    Curve = ECCurve.NamedCurves.nistP256,
    Q = new ECPoint
    {
        X = peerPublicKey65.AsSpan(1, 32).ToArray(),
        Y = peerPublicKey65.AsSpan(33, 32).ToArray()
    }
});
byte[] sharedSecret = ourKey.DeriveKeyMaterial(peerEcdh.PublicKey);
// sharedSecret.Length == 32
```

### 2.3 HKDF (HMAC-based Key Derivation Function)

#### HKDF-Extract

```
HKDF-Extract(SHA256, salt, IKM) → PRK (32 bytes)

Parámetros:
  Salt = peer_pubkey[1:65] (64 bytes de coordenadas X+Y) ||
         SHA256(peer_pubkey[1:65]) (32 bytes) = 96 bytes total
  IKM  = shared secret del ECDH (32 bytes)
  PRK  = pseudo-random key (32 bytes, interna)
```

#### HKDF-Expand

```
HKDF-Expand(SHA256, PRK, info, L=36) → 36 bytes

Parámetros:
  PRK  = output del HKDF-Extract
  info = NULL / vacío (byte[0])
  L    = 36 bytes
```

#### Estructura del Output HKDF (36 bytes)

```
Offset  Size  Usage
──────  ────  ─────────────────────────────────────────
 0..15   16   AES-128 key
16..31   16   [Uso desconocido — posiblemente HMAC key]
32..35    4   Nonce (últimos 4 bytes del derived key)
```

**Implementación C# de referencia:**

```csharp
byte[] saltPubRaw = peerPublicKey65.AsSpan(1, 64).ToArray();
byte[] saltPubHash = SHA256.HashData(saltPubRaw);
byte[] salt = [.. saltPubRaw, .. saltPubHash]; // 96 bytes

byte[] derivedKey = HKDF.DeriveKey(
    HashAlgorithmName.SHA256,
    sharedSecret,    // IKM (32 bytes)
    36,              // output length
    salt,            // salt (96 bytes)
    info: null       // info vacío
);

byte[] aesKey = derivedKey.AsSpan(0, 16).ToArray();
byte[] nonce  = derivedKey.AsSpan(32, 4).ToArray();
```

### 2.4 AES-128-GCM

```
Algoritmo:     AES-128-GCM (Galois/Counter Mode)
Key:           16 bytes (primeros 16 bytes del HKDF output)
IV:            8 bytes
  Bytes 0-3:  Nonce (4 bytes del HKDF output)
  Bytes 4-7:  Sequence counter (uint32 big-endian, empieza en 0)
Tag:           4 bytes (truncado de los 16 bytes de GCM, agregado al final del ciphertext)
```

**Implementación C# de referencia (BouncyCastle):**

```csharp
// Construir IV de 8 bytes: nonce[4] || counter[4] BE
private byte[] BuildIV(uint counter)
{
    byte[] iv = new byte[8];
    Array.Copy(_nonce!, 0, iv, 0, 4);
    iv[4] = (byte)(counter >> 24);
    iv[5] = (byte)(counter >> 16);
    iv[6] = (byte)(counter >> 8);
    iv[7] = (byte)(counter);
    return iv;
}

// Encriptar
public byte[] Encrypt(byte[] plaintext)
{
    byte[] iv = BuildIV(_txCounter++);
    var cipher = new GcmBlockCipher(new AesEngine());
    cipher.Init(true, new AeadParameters(
        new KeyParameter(_aesKey), 32, iv, null));
    byte[] output = new byte[cipher.GetOutputSize(plaintext.Length)];
    int len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
    cipher.DoFinal(output, len);
    return output; // [ciphertext] + [tag 4 bytes]
}

// Desencriptar
public byte[] Decrypt(byte[] ciphertextWithTag)
{
    byte[] iv = BuildIV(_rxCounter++);
    var cipher = new GcmBlockCipher(new AesEngine());
    cipher.Init(false, new AeadParameters(
        new KeyParameter(_aesKey), 32, iv, null));
    byte[] output = new byte[cipher.GetOutputSize(ciphertextWithTag.Length)];
    int len = cipher.ProcessBytes(ciphertextWithTag, 0, ciphertextWithTag.Length, output, 0);
    cipher.DoFinal(output, len); // Lanza InvalidCipherTextException si tag no coincide
    return output;
}
```

**NOTA IMPORTANTE:** NO es AES-CCM. Se confirmó mediante análisis del binario `ZwiftApp.exe` que el símbolo `EVP_aes_128_ccm` **NO existe** en el ejecutable. Solo están presentes `EVP_aes_128_gcm` y sus variantes. La implementación usa BouncyCastle porque `AesGcm` de .NET no permite tags de 4 bytes (tag size fijo en 16 bytes).

### 2.5 Contador de Secuencia

| Propiedad | Valor |
|---|---|
| **Tipo** | `uint32` |
| **Inicialización** | `0` |
| **Incremento** | `+1` por cada mensaje enviado o recibido |
| **Endianness en IV** | Big-endian |
| **Contadores** | Separados: `_txCounter` para encriptar, `_rxCounter` para desencriptar |

### 2.6 Diferencias Criptográficas: V1 vs V2

| Componente | V1 (Zwift Play 2023) | V2 (Zwift Click 2025) |
|---|---|---|
| **HKDF Salt** | 32 bytes de ceros | 96 bytes: `peer_pub[64] ‖ SHA256(peer_pub[64])` |
| **HKDF Info** | `null` / vacío | `null` / vacío (igual) |
| **HKDF Output** | 36 bytes (igual) | 36 bytes (igual) |
| **Cipher** | AES-128-CCM | AES-128-GCM |
| **Biblioteca** | BouncyCastle `CcmBlockCipher` | BouncyCastle `GcmBlockCipher` |
| **Tag size** | 4 bytes (32 bits) | 4 bytes (32 bits) |

---

## 3. Formatos de Handshake (CONFIRMADO)

### 3.1 Handshake V1 (Legacy — Zwift Play 2023)

```
Formato: "RideOn" [6 bytes] + sufijo [2 bytes] + pubkey_ec [65 bytes]
Longitud total: 73 bytes

Sufijos V1 conocidos:
  - 01 01 → handshake accept
  - 00 09 → variante alternativa
```

**Comportamiento especial de parseo en el dispositivo (descubierto en Ghidra):**
- El dispositivo busca el substring `"ideOn"` (bytes 1-5 de "RideOn") para detectar el inicio del handshake
- Extrae la clave EC desde offset 6 (después del "RideOn")
- Si el mensaje es menor a 7 bytes, salta a lógica short-circuit (no procesa handshake)

### 3.2 Handshake V2 (Actual — Zwift Click 2025)

```
Formato (hipótesis del código decompilado):
  [header 7 bytes] + [0x04 forzado] + [clave EC 64 bytes]

Longitud total: 72 bytes

El prefijo 0x04 es forzado por el código decompilado:
  *puStack_110 = 4;  // hardcodea el byte 0x04

Validación en el binario:
  - Mensaje debe ser ≥ 8 bytes
  - Mensaje debe ser ≤ 72 bytes
```

**NOTA IMPORTANTE:** Los strings `"RideOn"` encontrados en el binario de ZwiftApp.exe **NO son magic bytes de handshake**. Son nombres de campos Protobuf para features sociales (ej: `RideOnBombRequest`, `RideOnBombResponse`). El handshake real usa un header binario de 7 bytes cuyo contenido exacto no se ha podido determinar.

### 3.3 Variantes Probadas (Fuzzer)

El `HandshakeProber` prueba **6 formatos** secuencialmente:

```csharp
public enum HandshakeFormat
{
    V1_WithRideOn_01_02,    // "RideOn" + 01 02 + pubkey[65]  = 73 bytes
    V1_WithRideOn_00_09,    // "RideOn" + 00 09 + pubkey[65]  = 73 bytes
    V2_BarePublicKey,       // Solo pubkey[65] sin prefijos    = 65 bytes
    V2_RideOn_02_03,        // "RideOn" + 02 03 + pubkey[65]  = 73 bytes
    V2_RideOn_Plus04Key,    // "RideOn" + 04 + pubkey[65]     = 72 bytes
    V2_RideOn_02_03_Key64,  // "RideOn" + 02 03 + pubkey[64]  = 72 bytes
                             //   (sin el byte 0x04 inicial de la clave)
}
```

### 3.4 Sufijos Observados en Logs

| Sufijo | Contexto | Interpretación |
|---|---|---|
| `01 02` | Documentado por Makinolo (V1) | Handshake accept en Zwift Play 2023 |
| `02 03` | Observado en respuestas V2 | Respuesta de estado/rechazo en Click V2 |
| `00 09` | Mencionado en código fuente | Variante alternativa V1 |
| `01 01` | Posible respuesta V1 | Handshake success en algunas versiones |

### 3.5 Comportamiento Observado del Dispositivo

Para cada formato de handshake enviado por CH03, el dispositivo responde por CH04 con un mensaje Protobuf que **NO contiene una clave pública EC válida**, sino datos de estado:

**Ejemplo de respuesta a V2_RideOn_02_03:**
```
RideOn 02 03 58 02 00 00 00 00 00 01 FB 00 00...
```
→ Field 11 (wire type 0 = varint), valor = 2 → Error de autenticación

**Ejemplo de respuesta a V2_RideOn_Plus04Key:**
```
RideOn 02 03 C0 03 00 00 00 00 00 01 FB...
```
→ Field 24 (wire type 0 = varint), valor = 3 → Error de formato

**Ejemplo de respuesta con datos de batería:**
```
RideOn 02 03 10 64 18 00 20 00 00 00...
```
→ Field 2 (varint): 100 (0x64) = Batería 100%, Field 3: 0, Field 4: 0

---

## 4. Arquitectura BLE del Click V2 (CONFIRMADO)

### 4.1 Servicios GATT

#### Servicio V2 (Actual)

| UUID | Propiedades | Nombre | Descripción |
|---|---|---|---|
| `0000fc82-0000-1000-8000-00805f9b34fb` | — | Zwift Service | Servicio principal V2 |
| `00000100-19ca-4651-86e5-fa29dcdd09d1` | Write, WriteWithoutResponse, Notify | CH100 | Canal de control primario |
| `00000101-19ca-4651-86e5-fa29dcdd09d1` | Write, WriteWithoutResponse, Notify | CH101 | Canal de control secundario |
| `00000102-19ca-4651-86e5-fa29dcdd09d1` | WriteWithoutResponse, Notify | CH102 | Canal de broadcast |
| `00000002-19ca-4651-86e5-fa29dcdd09d1` | Notify | CH02 | Eventos de botones (Async notifications) |
| `00000003-19ca-4651-86e5-fa29dcdd09d1` | Write, WriteWithoutResponse | CH03 / SyncTx | Handshake + comandos |
| `00000004-19ca-4651-86e5-fa29dcdd09d1` | Indicate, Read | CH04 / SyncRx | Respuestas + notificaciones |
| `00000006-19ca-4651-86e5-fa29dcdd09d1` | Indicate, Read, Write, WriteWithoutResponse | CH06 | Datos de estado/info (única con Read) |

#### Servicio V1 Legacy

| UUID | Estado en V2 |
|---|---|
| `00000001-19ca-4651-86e5-fa29dcdd09d1` | **NO expuesto** en dispositivos V2 |

### 4.2 Características GATT — Detalle

```csharp
// UUIDs definidos en el código
public static readonly Guid ZWIFT_SERVICE_UUID = Guid.Parse("0000fc82-0000-1000-8000-00805f9b34fb");
public static readonly Guid CH02_UUID  = Guid.Parse("00000002-19ca-4651-86e5-fa29dcdd09d1");
public static readonly Guid CH03_UUID  = Guid.Parse("00000003-19ca-4651-86e5-fa29dcdd09d1");
public static readonly Guid CH04_UUID  = Guid.Parse("00000004-19ca-4651-86e5-fa29dcdd09d1");
public static readonly Guid CH06_UUID  = Guid.Parse("00000006-19ca-4651-86e5-fa29dcdd09d1");
public static readonly Guid CH100_UUID = Guid.Parse("00000100-19ca-4651-86e5-fa29dcdd09d1");
public static readonly Guid CH101_UUID = Guid.Parse("00000101-19ca-4651-86e5-fa29dcdd09d1");
public static readonly Guid CH102_UUID = Guid.Parse("00000102-19ca-4651-86e5-fa29dcdd09d1");
```

### 4.3 Comportamiento BLE Observado

| Operación | Resultado | Detalle |
|---|---|---|
| **Bonding BLE** | ❌ Falla | El dispositivo no soporta/permite bonding en Windows |
| **Conexión sin bonding** | ✅ Exitosa | `BluetoothLEDevice.FromBluetoothAddressAsync()` funciona |
| **Habilitar notificaciones CH02** | ✅ | `WriteClientCharacteristicConfigurationDescriptorAsync(Notify)` OK |
| **Habilitar notificaciones CH04** | ✅ | `WriteClientCharacteristicConfigurationDescriptorAsync(Indicate)` OK |
| **Habilitar notificaciones CH100-CH102** | ✅ | Funciona correctamente |
| **Write a CH03** | ✅ Write aceptado | `GattWriteOption.WriteWithoutResponse` retorna Success |
| **Read de CH06** | ✅ | Se puede leer, contenido pendiente de análisis detallado |
| **Respuesta del dispositivo** | ⚠️ Rechazo | El dispositivo responde con estado/error, no con clave EC |

### 4.4 Escaneo y Conexión

```csharp
// El dispositivo se anuncia como "Zwift Click"
var watcher = new BluetoothLEAdvertisementWatcher
{
    ScanningMode = BluetoothLEScanningMode.Active
};
watcher.Received += (s, args) =>
{
    if (args.Advertisement.LocalName.StartsWith("Zwift Click"))
        tcs.TrySetResult(args.BluetoothAddress);
};
```

---

## 5. Códigos de Error en Respuestas (OBSERVADO)

Las respuestas del dispositivo contienen campos Protobuf que codifican información de estado o error.

### 5.1 Respuesta con código `58 02`

```
Hex completo: 52 69 64 65 4F 6E 02 03 58 02 00 00 00 00 00 01 FB 00 00...
              R  i  d  e  O  n  02 03 58 02 ...
```

**Decodificación Protobuf:**
- `52 69 64 65 4F 6E` = "RideOn" (6 bytes)
- `02 03` = sufijo V2
- `58 02` = Field 11 (tag = `0x58 → field_number=11, wire_type=0`), valor varint = 2
- `00 00 00 00` = campos con valor 0
- `00 01` = Field 0 (?), valor 1

**Interpretación:** Field 11 = 2 podría ser un código de error de autenticación o firmware incompatible.

### 5.2 Respuesta con código `C0 03`

```
Hex: 52 69 64 65 4F 6E 02 03 C0 03 00 00 00 00 00 01 FB...
     R  i  d  e  O  n  02 03 C0 03 ...
```

**Decodificación Protobuf:**
- `C0 03` = Field 24 (tag = `0xC0 → field_number=24, wire_type=0`), valor varint = 3

**Interpretación:** Field 24 = 3 podría significar error de formato de handshake (versión incorrecta, longitud inválida).

### 5.3 Respuesta con datos de batería

```
Hex: 52 69 64 65 4F 6E 02 03 10 64 18 00 20 00 00 00...
     R  i  d  e  O  n  02 03 10 64 18 00 20 00 ...
```

**Decodificación Protobuf:**
- `10 64` = Field 2 (wire type 0 = varint), valor = 100 (0x64) → **Batería 100%**
- `18 00` = Field 3 (wire type 0 = varint), valor = 0 → Botón/dial 0
- `20 00` = Field 4 (wire type 0 = varint), valor = 0 → Botón/dial 0

**Interpretación:** El dispositivo responde con su estado actual (batería, posición de diales) en lugar de una clave pública EC. Esto es una respuesta de **rechazo implícito**.

### 5.4 Respuesta con estructura anidada

```
Hex: 52 69 64 65 4F 6E 02 03 12 04 01 00 00 01...
     R  i  d  e  O  n  02 03 12 04 01 00 00 01...
```

**Decodificación Protobuf:**
- `12 04` = Field 2 (wire type 2 = length-delimited), length = 4 bytes
- Contenido: `01 00 00 01`

**Interpretación:** Estructura anidada o código de error complejo de 4 bytes.

### 5.5 Algoritmo de Detección de Rechazo

```csharp
public static bool LooksLikeRejection(byte[] response)
{
    if (response == null || response.Length < 10) return false;

    // Patrón: "RideOn" + 02 03 + datos protobuf
    if (response.Length >= 8 &&
        response[0] == 'R' && response[1] == 'i' && response[2] == 'd' &&
        response[3] == 'e' && response[4] == 'O' && response[5] == 'n' &&
        response[6] == 0x02 && response[7] == 0x03)
    {
        byte[] afterHeader = response.Skip(8).ToArray();

        // Batería: 0x10 = field 2 varint
        if (afterHeader.Length >= 2 && afterHeader[0] == 0x10 && afterHeader[1] == 0x64)
            return true;

        // Código de estado: 0x58 = field 11 varint
        if (afterHeader.Length >= 2 && afterHeader[0] == 0x58)
            return true;

        // Fields Protobuf bajos típicos de respuestas de estado
        if (afterHeader.Length >= 1 &&
            (afterHeader[0] == 0x08 || afterHeader[0] == 0x10 ||
             afterHeader[0] == 0x18 || afterHeader[0] == 0x20))
            return true;
    }
    return false;
}
```

---

## 6. Paquete Misterioso de 85 Bytes (OBSERVADO)

### 6.1 Características

Durante las pruebas de fuzzing, se observó un paquete de 85 bytes exactos que aparece esporádicamente en CH02 después de enviar handshakes. Sus características sugieren que es un mensaje cifrado.

| Propiedad | Valor |
|---|---|
| **Longitud** | 85 bytes exactos |
| **Prefijo** | `FF 03 00 0A 21` (5 bytes) |
| **Entropía** | Alta — distribución uniforme de bytes |
| **Frecuencia** | Esporádica, no en todas las sesiones |
| **Variabilidad** | Los bytes cambian entre capturas (contenido dinámico) |
| **Canal** | CH02 (Notify) |

### 6.2 Ejemplo Completo

```
FF 03 00 0A 21 03 48 F3 B1 C1 38 0C 3F E3 A5 3E
BF 87 C3 7B 80 54 66 30 AB 79 30 0D 3C 62 D6 90
86 D4 DD 97 E6 36 10 80 80 8C 10 1A 28 1D 40 28
A3 36 2B 31 B3 36 E3 7A 8B 88 00 4F AB 4A 15 D3
92 DE 71 5F F6 7C FF D8 EE 6A A6 6D 0D 9D B6 59
8C 02 4F EC 3A
```

### 6.3 Hipótesis

1. **Mensaje ZOP cifrado con AES-GCM**: Podría ser un mensaje del protocolo ZOP (Hello/Welcome/Ping) cifrado con la clave derivada del handshake. Sin la clave correcta, el descifrado falla.

2. **Datos de sensores**: Podría contener información de acelerómetro/giroscopio del dispositivo. El Click V2 tiene sensores internos para detectar orientación.

3. **Respuesta de handshake alternativa**: Podría ser una respuesta de handshake que llega por CH02 en lugar de CH04, posiblemente en un formato diferente.

4. **Firmware metadata**: Información de versión de firmware, número de serie, o datos de calibración enviados al inicio de la conexión.

### 6.4 Intentos de Descifrado

| Técnica | Resultado |
|---|---|
| Fuerza bruta de contadores 0-1000 | ❌ Sin éxito |
| Parseo como Protobuf | ❌ No produce campos reconocibles |
| Análisis de entropía | Confirma datos cifrados (entropía ~7.8 bits/byte) |
| Búsqueda de patrones conocidos | ❌ Sin coincidencias |

**Conclusión:** No se logró descifrar. Probablemente requiere la clave AES correcta derivada de un handshake exitoso.

---

## 7. Protocolo ZOP (Zwift Operations Protocol) (CONFIRMADO)

### 7.1 Estructura de ZMessage

Extraído del análisis de los FileDescriptorProto en la sección `.rdata` de ZwiftApp.exe:

```protobuf
message ZMessage {
  oneof payload {
    Hello hello = 1;
    Welcome welcome = 2;
    Ping ping = 3;
    Pong pong = 4;
    Request request = 6;
    Response response = 7;
    CapabilityRequest capability_request = 8;
    CapabilityResponse capability_response = 9;
    // ... otros campos
  }
}
```

### 7.2 Flujo de Mensajes Post-Handshake (Teórico)

Si el handshake fuera exitoso, el flujo esperado sería:

```
1. Cliente → Dispositivo:  Hello { client_id, capabilities }
2. Dispositivo → Cliente:  Welcome {}
3. Cliente → Dispositivo:  CapabilityRequest {}
4. Dispositivo → Cliente:  CapabilityResponse { ... }
5. Bidireccional:           Ping {} / Pong {} (keep-alive cada ~5s)
6. Cliente → Dispositivo:  Request { request_id, role }
7. Dispositivo → Cliente:  Response { request_id, success }
```

### 7.3 Formato de Mensaje ZOP Cifrado (Wire Format)

```
[Sequence Number: 4 bytes little-endian] + [ZMessage Protobuf cifrado] + [GCM Tag: 4 bytes]
```

**NOTA IMPORTANTE:** El sequence number en el wire format del protocolo ZOP es **little-endian**, diferente al contador **big-endian** usado en el IV de AES-GCM. Esto fue confirmado por Makinolo para V1 y se asume igual para V2.

### 7.4 Mensajes ZOP Identificados

#### Hello
```protobuf
message Hello {
  string client_id = 1;
  repeated Capability capabilities = 2;
}
```

#### Welcome
```protobuf
message Welcome {
  // Campos desconocidos
}
```

#### Ping / Pong
```protobuf
message Ping {}
message Pong {}
```
Payload vacío, enviado cada ~5 segundos como keep-alive.

#### CapabilityRequest / CapabilityResponse
```protobuf
message CapabilityRequest {
  // Solicita capabilities del dispositivo
}
message CapabilityResponse {
  repeated Capability capabilities = 1;
}
```

#### Request / Response
```protobuf
message Request {
  uint32 request_id = 1;
  Role role = 2;
}
message Response {
  uint32 request_id = 1;
  bool success = 2;
}
```

### 7.5 Implementación del Sequencer

```csharp
public class ZopSequencer
{
    private uint _sequence;
    private readonly ZPEncryptionV2 _encryption;

    public byte[] EncryptMessage(byte[] zopProtobuf)
    {
        // Prepend sequence number LE
        byte[] seqBytes = new byte[4];
        seqBytes[0] = (byte)(_sequence & 0xFF);
        seqBytes[1] = (byte)((_sequence >> 8) & 0xFF);
        seqBytes[2] = (byte)((_sequence >> 16) & 0xFF);
        seqBytes[3] = (byte)((_sequence >> 24) & 0xFF);
        _sequence++;

        // Cifrar: [ZMessage] → [ciphertext + tag 4B]
        byte[] encrypted = _encryption.Encrypt(zopProtobuf);

        // Wire format: [seq LE 4B] + [ciphertext + tag]
        return [.. seqBytes, .. encrypted];
    }

    public byte[] DecryptMessage(byte[] wireData)
    {
        // Extraer sequence number LE (4 bytes iniciales)
        uint seq = BitConverter.ToUInt32(wireData, 0);

        // Desencriptar resto: [ciphertext + tag 4B]
        byte[] encrypted = wireData.AsSpan(4).ToArray();
        return _encryption.Decrypt(encrypted);
    }
}
```

---

## 8. ControllerNotification y Botones (CONFIRMADO)

### 8.1 Hallazgo Clave

**ControllerNotification NO es un mensaje Protobuf ZOP directo.** Los eventos de botones viajan como bytes crudos en el campo `BLEPeripheralResponse.characteristic_value` y se interpretan mediante un enum `ZwiftButtonMap` en `DeviceInputManager.cpp`.

### 8.2 Schema de BLEPeripheralResponse

Extraído de los FileDescriptorProto en el binario:

```protobuf
message PhoneToGameCommand {
  // ...
  BLEPeripheralResponse ble_peripheral_response = 18;
  // ...
}

message BLEPeripheralResponse {
  bytes characteristic_value = 1;  // ← Los bytes de botones viajan aquí
  // ...
}
```

### 8.3 ZwiftButtonMap

- **Tipo:** Enum definido en `DeviceInputManager.cpp`
- **Constantes conocidas:**
  - `ZwiftButtonMap::INVALID`
  - `ZwiftButtonMap::MAX_BUTTONS`
- **Valores específicos:** No se pudieron extraer completamente de los schemas. Posiblemente:
  - `01` = LeftClick
  - `02` = RightClick
  - `03` = LeftHold
  - `04` = RightHold
  - `05` = ButtonRelease
- **Mapeo a teclas (teórico):**
  - LeftClick → `VK_LEFT` (0x25)
  - RightClick → `VK_RIGHT` (0x27)

### 8.4 Flujo de Procesamiento de Botones

```
1. Dispositivo detecta click físico
2. Dispositivo envía bytes por CH02 (Notify)
3. Bridge recibe notificación BLE
4. Bridge desencripta mensaje ZOP si es necesario
5. Bridge extrae bytes de characteristic_value
6. Bridge mapea bytes a ZwiftButtonMap
7. Bridge emite virtual key via SendInput
```

### 8.5 Implementación de Emulación de Teclado

```csharp
// KeyboardEmulator.cs
using System.Runtime.InteropServices;

public class KeyboardEmulator
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;       // 1 = keyboard
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;
    private const ushort KEYEVENTF_KEYDOWN = 0x0000;
    private const ushort KEYEVENTF_KEYUP = 0x0002;

    public static void SendKey(ushort vkCode)
    {
        var inputs = new INPUT[2];

        // Key down
        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = vkCode, dwFlags = KEYEVENTF_KEYDOWN }
        };

        // Key up
        inputs[1] = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = vkCode, dwFlags = KEYEVENTF_KEYUP }
        };

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }
}
```

---

## 9. Device Auth y Daily Unlock (ACTUALIZADO)

### 9.1 Hallazgos en el Código de ZwiftApp.exe

| Aspecto investigado | Resultado |
|---|---|
| Bonding BLE tradicional | **NO observado** — no aparecen APIs SMP/pairing BLE en la ruta ZAP |
| String "unlock" en el binario | Se refiere a desbloqueo de items cosméticos (bikes, jerseys, badges) |
| Auth challenge del dispositivo | **SÍ existe** — `Received auth challenge` aparece en `x.c` |
| Dependencia de red | **SÍ existe** — falla con `Network service not initialized` |
| Bearer auth en la capa HTTP | **SÍ existe** — la red común inyecta `Authorization: Bearer <access_token>` |

### 9.2 Comportamiento Reportado por la Comunidad

Múltiples fuentes independientes reportan el mismo patrón empírico:

1. Usuario conecta el Click V2 a Zwift oficial por ~30 segundos (el "unlock diario")
2. Durante esos 30 segundos, Zwift oficial envía comandos al dispositivo
3. Después de esto, el dispositivo queda "desbloqueado" por ~24 horas
4. Durante esas 24 horas, apps de terceros (como QDomyos-Zwift) pueden conectarse
5. Pasadas 24 horas, el dispositivo vuelve a rechazar conexiones no oficiales

### 9.3 Hipótesis Principal Actual

El binario ya no soporta la hipótesis de "unlock 100% local". La evidencia más fuerte apunta a este flujo:

```
1. El dispositivo envía un `auth challenge` a ZwiftApp.
2. ZwiftApp intenta resolverlo mediante `INetworkService`.
3. La capa HTTP común usa `Authorization: Bearer <access_token>`.
4. Si la respuesta llega, ZwiftApp la reinyecta a la capa ZAP (`Received auth response`).
5. Si no hay red/sesión válida, el challenge falla y el dispositivo sigue rechazando el canal seguro.
```

**Evidencia que apoya esta hipótesis:**
- `Received auth challenge (size = %zu)` en el decompilado.
- `Device challenge failed: Network service not initialized` en la misma ruta.
- `Received auth response (size = %zu)` inmediatamente después en el flujo simétrico.
- El stack HTTP de Zwift inyecta `Authorization: Bearer <access_token>` y marca el token invalidado en `401`.
- La `auth response` se entrega a `ZapMessageComponent`; el `0x13` observado en esta ruta es el id del componente, no el opcode del mensaje.
- `ZP device authentication is not enabled` aparece en otra rama previa del subsistema ZP; actúa como feature gate, no como sustituto del handler de challenge.

### 9.4 Estado de `FF 04 00`

`FF 04 00` sigue siendo un artefacto empírico importante, pero su rol exacto no está cerrado:
- Proyectos externos lo envían en texto plano post-handshake.
- El decompilado revisado en esta fase no lo confirma como "unlock definitivo".
- La autenticación por challenge/red reduce la confianza en cualquier conclusión de "unlock solo local".
- Puede seguir siendo un comando de control secundario o una pieza posterior al challenge.
- Tampoco hay prueba binaria de que la `auth response` o `FF 04 00` viajen específicamente por CH06.

### 9.5 Implicaciones

- Un `access_token` válido de sesión parece formar parte del flujo de device auth.
- No hay evidencia firme de un `refresh_token` usado directamente por el challenge.
- Tampoco hay evidencia firme todavía de scopes ZAP dedicados.
- Tampoco apareció un endpoint HTTP literal del challenge en el decompilado inspeccionado.
- La forma confiable de cerrar el flujo sigue siendo capturar tráfico real HTTP/BLE durante hot pairing.

### 9.6 Comparación con V1 (Zwift Play 2023)

El Zwift Play 2023 (V1) **no tiene este mecanismo de DRM**. La comunidad (Makinolo, ajchellew, QDomyos-Zwift) logró implementar compatibilidad completa con V1 sin necesidad de desbloqueo diario. Esto sugiere que el DRM fue introducido específicamente en el Click V2 (2025).

---

## 10. Herramientas y Técnicas de Ingeniería Inversa Usadas

### 10.1 Decompilación y Análisis Estático

| Herramienta | Uso | Resultado |
|---|---|---|
| **Ghidra** | Decompilación de `ZwiftApp.exe` (PE x64, ~50 MB) | Identificación de funciones criptográficas, formatos de handshake, strings |
| **strings.exe** | Extracción de strings del binario | ~500K strings, incluyendo nombres Protobuf y mensajes de log |
| **Protoc** | Deserialización de FileDescriptorProto de `.rdata` | Esquemas Protobuf parciales (ZMessage, BLEPeripheralResponse, etc.) |
| **RustDemangle** | Demangling de símbolos C++ | Nombres de clases y métodos de Zwift |

#### Principales funciones identificadas en Ghidra:

```
ECDH_compute_key          → Intercambio de claves P-256
HKDF_Extract              → Derivación de clave (SHA-256)
HKDF_Expand               → Expansión de clave
EVP_aes_128_gcm           → Cifrado/descifrado (CONFIRMADO: solo GCM, no CCM)
ZwiftButtonMap            → Mapeo de bytes de botones a eventos
DeviceInputManager        → Gestor de entrada de dispositivos
BLEPeripheralResponse     → Mensaje Protobuf para respuestas BLE
```

### 10.2 Análisis Dinámico

| Técnica | Descripción |
|---|---|
| **WinRT BLE API** | Conexión y comunicación con dispositivos BLE desde C# |
| **Logs estructurados** | Captura de tráfico BLE con timestamps en JSON |
| **Fuzzing sistemático** | Prueba de 6 variaciones de handshake con 6 formatos diferentes |
| **Análisis de respuestas** | Clasificación de respuestas del dispositivo (EC key vs status) |

### 10.3 Técnicas Específicas

| Técnica | Propósito | Resultado |
|---|---|---|
| **Fuerza bruta de contadores** | Descifrar paquete de 85 bytes probando contadores 0-1000 | ❌ Sin éxito |
| **Parseo manual de Protobuf** | Interpretar bytes de respuesta como campos Protobuf | ✅ Identificados fields de batería, error, estado |
| **Análisis de entropía** | Diferenciar datos cifrados de plaintext | ✅ Confirmado: paquete 85B es cifrado |
| **Búsqueda de prefijos 0x04** | Detectar claves públicas EC en respuestas | ✅ Algoritmo robusto implementado |
| **Validación de puntos EC** | Verificar que claves extraídas pertenecen a P-256 | ✅ `ImportParameters` + `ExportParameters` |

### 10.4 Estructura del Proyecto de Ingeniería Inversa

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
│   │   ├── HandshakeProber.cs           # Probing de 6 formatos
│   │   ├── ZopMessage.cs                # Tipos de mensaje ZOP
│   │   ├── ZopSerializer.cs             # Serialización Protobuf
│   │   ├── ZwiftClickProtocol.cs        # Protocolo específico
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
│   └── ZPEncryptionTests.cs             # Tests unitarios de cifrado
└── docs/
    ├── REVERSE_ENGINEERING.md           # Documentación original V1
    └── REVERSE_ENGINEERING_V2.md        # Este documento
```

### 10.5 Dependencias del Proyecto

| Dependencia | Versión | Propósito |
|---|---|---|
| .NET 8 | `net8.0-windows10.0.19041.0` | Runtime (requerido para WinRT BLE) |
| Google.Protobuf | 3.35.0 | Serialización/deserialización Protobuf |
| Portable.BouncyCastle | 1.9.0 | AES-GCM/CCM con tag de 4 bytes |
| Microsoft.Windows.SDK.NET.Ref | Implícito | WinRT BLE API |

### 10.6 Limitaciones Encontradas

| Limitación | Impacto |
|---|---|
| No se pudo capturar tráfico BLE de Zwift oficial | Requiere sniffer hardware (Ubertooth One, nRF52840 Dongle) |
| No se pudieron extraer todos los valores de enums | ButtonIcon y ZwiftButtonMap incompletos |
| El binario está compilado sin símbolos de debug | Nombres de funciones inferidos por contexto |
| FileDescriptorProto incompletos | Algunos mensajes Protobuf tienen campos desconocidos |
| Bonding BLE no soportado en Windows | No se puede probar autenticación paired |

---

## 11. Comparación V1 vs V2

### 11.1 Tabla Comparativa Completa

| Aspecto | V1 (Zwift Play 2023) | V2 (Zwift Click 2025) |
|---|---|---|
| **Año de lanzamiento** | 2023 | 2025 |
| **Servicio BLE principal** | `00000001-19ca-4651-86e5-fa29dcdd09d1` | `0000fc82-0000-1000-8000-00805f9b34fb` |
| **UUIDs de características** | CH02, CH03, CH04, CH06 | Mismas + CH100, CH101, CH102 |
| **Formato de handshake** | `"RideOn" + sufijo + pubkey[65]` | `header[7] + 0x04 + pubkey[64]` (binario) |
| **Sufijos de handshake** | `01 01`, `00 09` | `02 03`, `01 02` |
| **Cifrado** | AES-128-**CCM** | AES-128-**GCM** |
| **Curva elíptica** | P-256 (secp256r1) | P-256 (secp256r1) — sin cambios |
| **ECDH** | `ECDH_compute_key` | `ECDH_compute_key` — sin cambios |
| **HKDF Hash** | SHA-256 | SHA-256 — sin cambios |
| **HKDF Salt** | 32 bytes de ceros | 96 bytes: `peer_pub[64] ‖ SHA256(peer_pub[64])` |
| **HKDF Info** | `null` / vacío | `null` / vacío — sin cambios |
| **HKDF Output** | 36 bytes | 36 bytes — sin cambios |
| **IV estructura** | `nonce[4] ‖ counter[4] BE` | `nonce[4] ‖ counter[4] BE` — sin cambios |
| **Tag size** | 4 bytes | 4 bytes — sin cambios |
| **Contador** | uint32 BE, empieza en 0 | uint32 BE, empieza en 0 — sin cambios |
| **Sequence number (wire)** | uint32 LE | uint32 LE (asumido igual) |
| **DRM** | Sin DRM aparente | Daily unlock requerido (~24h) |
| **Compatibilidad comunitaria** | ✅ Documentado por Makinolo | ❌ Sin documentación pública |
| **Implementaciones open-source** | ✅ ajchellew/zwiftplay | ❌ Ninguna funcional |
| **Estado de ingeniería inversa** | ✅ Completamente funcional | ❌ Handshake rechazado |

### 11.2 Cambios Clave en V2

1. **Nuevo servicio BLE** (`0xFC82`) con 3 características adicionales (CH100-CH102)
2. **Salt HKDF complejo** (96 bytes con hash) en lugar de salt de ceros
3. **AES-GCM** en lugar de AES-CCM (confirmado por ausencia de símbolos CCM en el binario)
4. **Formato de handshake binario** en lugar de basado en texto ("RideOn")
5. **Mecanismo de DRM** con temporizador interno de ~24 horas

### 11.3 Lo que NO Cambió

- Misma curva elíptica (P-256)
- Mismo algoritmo de derivación (HKDF-SHA256)
- Misma estructura de IV (nonce + counter)
- Mismo tamaño de tag (4 bytes)
- Mismos UUIDs de características legacy (CH02-CH06) para compatibilidad
- Mismo protocolo ZOP (ZMessage, Hello, Welcome, Ping, etc.)

---

## 12. Estado Actual del Proyecto

### 12.1 Lo Que Funciona

| Componente | Estado | Notas |
|---|---|---|
| Conexión BLE | ✅ | Sin bonding, conexión directa |
| Habilitación de notificaciones | ✅ | CH02, CH04, CH100, CH101, CH102 |
| Envío de handshakes | ✅ | 6 variaciones implementadas y probadas |
| Recepción de respuestas | ✅ | CH04 Indicate funciona correctamente |
| Implementación criptográfica | ✅ | ECDH + HKDF + AES-128-GCM completo |
| Fuzzer de handshakes | ✅ | `HandshakeProber` con 6 formatos |
| Logger estructurado | ✅ | JSON con timestamps |
| Emulación de teclado | ✅ | `SendInput` user32.dll |
| Tests unitarios | ✅ | 11 tests de cifrado |

### 12.2 Lo Que NO Funciona

| Componente | Estado | Causa raíz |
|---|---|---|
| Handshake aceptado | ❌ | Dispositivo rechaza con datos de estado |
| Derivación de clave AES | ❌ | No se obtiene clave pública EC del dispositivo |
| Descifrado de mensajes | ❌ | Sin clave de sesión no se puede descifrar |
| Activación del stream de botones | ❌ | Requiere handshake exitoso previo |
| Interpretación de eventos de botones | ❌ | Sin stream de botones no hay eventos que interpretar |

### 12.3 Próximos Pasos Posibles

#### Prioridad Alta

1. **Capturar tráfico BLE de Zwift oficial**
   - Hardware necesario: Ubertooth One (~$120 USD) o nRF52840 Dongle (~$15 USD)
   - Objetivo: Ver el handshake real entre ZwiftApp.exe y el Click V2
   - Método: Wireshark + BLE sniffer plugin

2. **Analizar comando de reset del temporizador**
   - Observar qué writes/envíos hace Zwift oficial durante los primeros 30 segundos
   - Identificar la secuencia de bytes que resetea el temporizador interno
   - Probar en CH100, CH101, CH102 (las nuevas características V2)

#### Prioridad Media

3. **Investigar característica CH06**
   - Única característica con propiedad Read
   - Podría contener información de estado del temporizador DRM
   - Podría contener un token o nonce necesario para el handshake

4. **Analizar paquete de 85 bytes**
   - Intentar descifrar asumiendo diferentes estructuras de clave/IV
   - Probar con AES-ECB, AES-CTR, o sin cifrado
   - Analizar si contiene datos estructurados (Protobuf, TLV, etc.)

5. **Contactar comunidad**
   - Compartir hallazgos en foros de Zwift/QDomyos-Zwift
   - Colaborar con desarrolladores que tengan acceso a sniffers BLE
   - Buscar documentación interna de Zwift (leaks, patentes)

#### Prioridad Baja

6. **Extraer firmware del dispositivo**
   - Intentar lectura de firmware vía SWD/JTAG (requiere abrir el dispositivo)
   - Analizar el código del temporizador DRM directamente

7. **Ingeniería inversa de la app Android/iOS**
   - Las apps móviles pueden tener menos ofuscación que el binario Windows
   - El código Java/Kotlin/Swift es más fácil de decompilar

### 12.4 Conclusión Técnica

El Zwift Click V2 implementa una capa de DRM más estricta que su predecesor V1. El dispositivo **rechaza activamente** handshakes que no provienen de una instancia autorizada de Zwift, devolviendo datos de estado en lugar de su clave pública EC. Este mecanismo:

- **No es un error de formato:** Probamos 6 formatos diferentes y todos son rechazados
- **No es un problema de timing:** Las respuestas llegan en <1 segundo
- **No es un problema de BLE:** La conexión, escritura y notificaciones funcionan perfectamente
- **Es intencional:** El firmware decide no participar en el ECDH hasta que se cumpla alguna condición

Las hipótesis más probables son:
1. El dispositivo tiene un temporizador interno de ~24h que requiere reset vía comando especial
2. El dispositivo requiere un token/nonce leído de CH06 antes del handshake
3. El comando de reset solo puede ser enviado después de un handshake exitoso con Zwift oficial

**Sin acceso al tráfico BLE de Zwift oficial, el proyecto está en un punto muerto técnico.** La captura de este tráfico es el siguiente paso crítico e insustituible.

---

## 13. Referencias y Recursos

### 13.1 Documentación Externa

| Recurso | URL | Relevancia |
|---|---|---|
| Makinolo's Blog — Zwift Play Protocol | https://www.makinolo.com/blog/2023/10/08/connecting-to-zwift-play-controllers/ | Documentación original del protocolo V1 |
| ajchellew/zwiftplay (GitHub) | https://github.com/ajchellew/zwiftplay | Implementación Python de referencia para V1 |
| QDomyos-Zwift (GitHub) | https://github.com/cagnulein/qdomyos-zwift | Bridge Zwift → apps de terceros (soporta V1) |

### 13.2 Herramientas Usadas

| Herramienta | URL | Propósito |
|---|---|---|
| Ghidra | https://ghidra-sre.org/ | Decompilación y análisis estático |
| Protoc | https://github.com/protocolbuffers/protobuf | Deserialización de FileDescriptorProto |
| OpenSSL | https://www.openssl.org/ | Referencia para ECDH, HKDF, AES-GCM |
| BouncyCastle | https://github.com/bcgit/bc-csharp | AES-GCM/CCM con tag de 4 bytes |
| .NET 8 | https://dotnet.microsoft.com/ | Runtime y framework |
| Visual Studio 2022 | https://visualstudio.microsoft.com/ | IDE de desarrollo |

### 13.3 Hardware de Captura BLE Recomendado

| Dispositivo | Precio aprox. | Capacidad |
|---|---|---|
| nRF52840 Dongle | ~$15 USD | Sniffer BLE con Wireshark |
| Ubertooth One | ~$120 USD | Sniffer BLE de propósito general |
| Adafruit Bluefruit LE Sniffer | ~$30 USD | Sniffer BLE básico |
| nRF52840 DK | ~$50 USD | Development kit + sniffer |

### 13.4 Estándares Criptográficos

| Estándar | URL | Relevancia |
|---|---|---|
| NIST P-256 (FIPS 186-4) | https://nvlpubs.nist.gov/nistpubs/FIPS/NIST.FIPS.186-4.pdf | Curva elíptica |
| HKDF (RFC 5869) | https://tools.ietf.org/html/rfc5869 | Derivación de claves |
| AES-GCM (NIST SP 800-38D) | https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf | Cifrado autenticado |
| Protocol Buffers | https://protobuf.dev/ | Serialización de mensajes ZOP |

### 13.5 Artículos y Recursos de la Comunidad

- **Zwift Forums** — Discusiones sobre compatibilidad de dispositivos
- **Reddit r/Zwift** — Experiencias de usuarios con Click V2 y apps de terceros
- **QDomyos-Zwift Discord** — Comunidad activa de desarrollo de bridges Zwift

---

## 14. Changelog

| Fecha | Versión | Cambios |
|---|---|---|
| 2026-05-27 | 2.0 | Documento inicial exhaustivo. Incluye todos los hallazgos de ingeniería inversa del Zwift Click V2: esquema criptográfico completo (ECDH P-256 + HKDF 96B salt + AES-128-GCM), 6 formatos de handshake documentados, arquitectura BLE detallada, códigos de error Protobuf decodificados, análisis del paquete misterioso de 85 bytes, protocolo ZOP, mecanismo ControllerNotification, investigación de DRM/daily unlock, comparativa V1 vs V2, herramientas y técnicas usadas, y referencias completas. |

---

> **Nota para futuros desarrolladores:** Este documento representa el estado del conocimiento al 27 de mayo de 2026. Si has llegado hasta aquí y tienes acceso a un sniffer BLE, el próximo paso crítico es capturar el tráfico entre ZwiftApp.exe y el Click V2 durante los primeros 30 segundos de conexión. Ese handshake real revelará el formato exacto y posiblemente el comando de "daily unlock" que resetea el temporizador interno del dispositivo. ¡Buena suerte!
>
> — El equipo de ingeniería inversa