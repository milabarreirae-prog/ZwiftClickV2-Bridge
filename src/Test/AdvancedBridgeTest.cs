using System.Security.Cryptography;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using ZwiftClickV2.Bridge.BLE;

namespace ZwiftClickV2.Bridge.Test;

/// <summary>
/// Test avanzado de 3 fases: Unlock → ECDH Handshake → Captura.
/// Hipótesis: El comando FF 04 00 (Unlock) pone al dispositivo en un estado
/// donde SÍ acepta el handshake ECDH y devuelve su clave pública.
/// </summary>
public static class AdvancedBridgeTest
{
    private static readonly Guid CH02_UUID = BleDeviceManager.CH02_UUID;
    private static readonly Guid CH03_UUID = BleDeviceManager.CH03_UUID;
    private static readonly Guid CH04_UUID = BleDeviceManager.CH04_UUID;

    // Acumuladores para diagnóstico
    private static readonly List<CapturedPacket> _ch02Packets = new();
    private static readonly List<CapturedPacket> _ch04Packets = new();
    private static bool _detected0x37;
    private static string _detected0x37Channel = "";

    public static async Task RunAsync(GattDeviceService service)
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  Advanced Bridge Test");
        Console.WriteLine("  Unlock → ECDH Handshake → Capture");
        Console.WriteLine("═══════════════════════════════════════════\n");

        // ── Resolver características ──
        var charsResult = await service.GetCharacteristicsAsync();
        if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics == null)
        {
            Console.WriteLine("❌ No se pudieron obtener características.");
            return;
        }

        var chars = charsResult.Characteristics.ToList();
        Console.WriteLine($"Características encontradas: {chars.Count}");
        foreach (var c in chars)
            Console.WriteLine($"   {c.Uuid} — Properties: {c.CharacteristicProperties}");

        var ch02 = chars.FirstOrDefault(c => c.Uuid == CH02_UUID);
        var ch03 = chars.FirstOrDefault(c => c.Uuid == CH03_UUID);
        var ch04 = chars.FirstOrDefault(c => c.Uuid == CH04_UUID);

        Console.WriteLine($"\nCH02 (Async/0x0002): {(ch02 != null ? "✅" : "❌")}");
        Console.WriteLine($"CH03 (SyncRx/0x0003): {(ch03 != null ? "✅" : "❌")}");
        Console.WriteLine($"CH04 (SyncTx/0x0004): {(ch04 != null ? "✅" : "❌")}");

        if (ch03 == null || ch04 == null)
        {
            Console.WriteLine("❌ CH03 y CH04 son requeridas. Abortando.");
            return;
        }

        // ── Suscribir CH04 con Indicate desde el inicio ──
        Console.WriteLine("\n🔹 Suscribiendo CH04 (Indicate)...");
        try
        {
            await ch04.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Indicate);
            Console.WriteLine("   ✅ CH04 suscrito");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Error: {ex.Message}");
            return;
        }

        ch04.ValueChanged += (s, e) =>
        {
            var r = DataReader.FromBuffer(e.CharacteristicValue);
            var d = new byte[r.UnconsumedBufferLength];
            r.ReadBytes(d);
            _ch04Packets.Add(new CapturedPacket(DateTime.Now, d));
            string hex = BitConverter.ToString(d).Replace("-", " ");
            Console.WriteLine($"📨 [CH04] {d.Length}B: {hex}");
            if (d.Length > 0 && d[0] == 0x37)
            {
                _detected0x37 = true;
                _detected0x37Channel = "CH04";
            }
        };

        // ── FASE 1: Unlock ──
        Console.WriteLine("\n═══ FASE 1: UNLOCK ═══");
        await ExecuteUnlockPhase(ch03);
        await Task.Delay(400);

        // ── FASE 2: ECDH Handshake ──
        Console.WriteLine("\n═══ FASE 2: ECDH HANDSHAKE ═══");
        byte[]? ourPubKey64 = await ExecuteEcdhPhase(ch03);
        await Task.Delay(400);

        // ── FASE 3: Captura ──
        Console.WriteLine("\n═══ FASE 3: CAPTURA (10s) ═══");
        await ExecuteCapturePhase(ch02);

        // ── DIAGNÓSTICO ──
        PrintDiagnostic(ourPubKey64);
    }

    /// <summary>
    /// Fase 1: Enviar RideOn → FF 04 00 (Unlock).
    /// </summary>
    private static async Task ExecuteUnlockPhase(GattCharacteristic ch03)
    {
        // Paso 1: RideOn
        Console.WriteLine("🔹 Enviando 'RideOn'...");
        byte[] rideOn = new byte[] { 0x52, 0x69, 0x64, 0x65, 0x4F, 0x6E };
        await WriteNoResponseAsync(ch03, rideOn);
        Console.WriteLine("   ✅ RideOn enviado. Esperando 300ms...");
        await Task.Delay(300);

        // Mostrar respuesta si llegó algo en CH04
        if (_ch04Packets.Count > 0)
        {
            var last = _ch04Packets.Last();
            Console.WriteLine($"   📥 Última respuesta CH04: {BitConverter.ToString(last.Data).Replace("-", " ")}");
        }

        // Paso 2: Unlock
        Console.WriteLine("🔹 Enviando Unlock (FF 04 00)...");
        byte[] unlock = new byte[] { 0xFF, 0x04, 0x00 };
        await WriteNoResponseAsync(ch03, unlock);
        Console.WriteLine("   ✅ Unlock enviado. Esperando 500ms...");
        await Task.Delay(500);

        if (_ch04Packets.Count > 0)
        {
            var last = _ch04Packets.Last();
            string hex = BitConverter.ToString(last.Data).Replace("-", " ");
            Console.WriteLine($"   📥 Última respuesta CH04: {hex}");

            // Detectar eco de RideOn (ACK)
            if (last.Data.Length >= 6 &&
                last.Data[0] == 'R' && last.Data[1] == 'i' && last.Data[2] == 'd' &&
                last.Data[3] == 'e' && last.Data[4] == 'O' && last.Data[5] == 'n')
            {
                Console.WriteLine("   ℹ️  Eco 'RideOn' detectado → ACK del dispositivo.");
            }
        }
        else
        {
            Console.WriteLine("   ⚠️  Sin respuesta en CH04 tras Unlock.");
        }
    }

    /// <summary>
    /// Fase 2: Handshake ECDH con payload de 72 bytes.
    /// Formato: "RideOn" (6B) + 0x01 0x02 (2B) + pubkey sin prefijo 0x04 (64B) = 72B.
    /// </summary>
    private static async Task<byte[]?> ExecuteEcdhPhase(GattCharacteristic ch03)
    {
        // Generar par efímero P-256
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdh.ExportParameters(false);
        byte[] pubKey64 = new byte[64];
        Array.Copy(parameters.Q.X!, 0, pubKey64, 0, 32);
        Array.Copy(parameters.Q.Y!, 0, pubKey64, 32, 32);

        Console.WriteLine($"🔑 Nuestra clave pública (64B): {BitConverter.ToString(pubKey64.Take(16).ToArray()).Replace("-", "")}...");

        // Construir payload: "RideOn" + 01 02 + pubkey[64]
        byte[] payload = new byte[72];
        // "RideOn"
        payload[0] = 0x52; payload[1] = 0x69; payload[2] = 0x64;
        payload[3] = 0x65; payload[4] = 0x4F; payload[5] = 0x6E;
        // Sufijo 01 02
        payload[6] = 0x01;
        payload[7] = 0x02;
        // Clave pública (64 bytes sin prefijo 0x04)
        Array.Copy(pubKey64, 0, payload, 8, 64);

        Console.WriteLine($"📤 Payload ECDH ({payload.Length}B):");
        Console.WriteLine($"   Header: {BitConverter.ToString(payload.Take(8).ToArray()).Replace("-", " ")}");
        Console.WriteLine($"   PubKey: {BitConverter.ToString(payload.Skip(8).Take(16).ToArray()).Replace("-", "")}...");

        int packetsBefore = _ch04Packets.Count;
        await WriteNoResponseAsync(ch03, payload);
        Console.WriteLine("   ✅ Handshake ECDH enviado. Esperando 1000ms...");
        await Task.Delay(1000);

        // Mostrar respuestas nuevas en CH04
        int newPackets = _ch04Packets.Count - packetsBefore;
        if (newPackets > 0)
        {
            Console.WriteLine($"   📥 {newPackets} nueva(s) respuesta(s) en CH04:");
            for (int i = packetsBefore; i < _ch04Packets.Count; i++)
            {
                string hex = BitConverter.ToString(_ch04Packets[i].Data).Replace("-", " ");
                Console.WriteLine($"      [{i}] {_ch04Packets[i].Data.Length}B: {hex}");
            }
        }
        else
        {
            Console.WriteLine("   ⚠️  Sin respuesta en CH04 tras handshake ECDH.");
        }

        return pubKey64;
    }

    /// <summary>
    /// Fase 3: Captura de 10 segundos en CH02 y CH04.
    /// </summary>
    private static async Task ExecuteCapturePhase(GattCharacteristic? ch02)
    {
        // Suscribir CH02 si existe
        if (ch02 != null)
        {
            Console.WriteLine("🔹 Suscribiendo CH02 (Notify) para eventos de botones...");
            try
            {
                await ch02.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                Console.WriteLine("   ✅ CH02 suscrito");

                ch02.ValueChanged += (s, e) =>
                {
                    var r = DataReader.FromBuffer(e.CharacteristicValue);
                    var d = new byte[r.UnconsumedBufferLength];
                    r.ReadBytes(d);
                    _ch02Packets.Add(new CapturedPacket(DateTime.Now, d));
                    string hex = BitConverter.ToString(d).Replace("-", " ");
                    Console.WriteLine($"🎮 [CH02] {d.Length}B: {hex}");
                    if (d.Length > 0 && d[0] == 0x37)
                    {
                        _detected0x37 = true;
                        _detected0x37Channel = "CH02";
                        Console.WriteLine("   ✅ ¡DETECTADO OPCODE 0x37! Protocolo de botones activo.");
                    }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Error CH02: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("⚠️  CH02 no disponible. Solo se capturará CH04.");
        }

        Console.WriteLine("\n⏳ Capturando por 10 segundos...");
        Console.WriteLine("   (presiona botones del Click V2 si quieres ver eventos)\n");

        for (int i = 10; i > 0; i--)
        {
            Console.Write($"\r   ⏱️  {i}s restantes...  ");
            await Task.Delay(1000);
        }
        Console.WriteLine();
        Console.WriteLine("   ✅ Captura completada.");
    }

    /// <summary>
    /// Diagnóstico final: analiza todos los paquetes capturados.
    /// </summary>
    private static void PrintDiagnostic(byte[]? ourPubKey64)
    {
        Console.WriteLine("\n═══════════════════════════════════════════");
        Console.WriteLine("  DIAGNÓSTICO FINAL");
        Console.WriteLine("═══════════════════════════════════════════");

        // ── Total de paquetes ──
        Console.WriteLine($"\n📊 Paquetes capturados:");
        Console.WriteLine($"   CH04: {_ch04Packets.Count}");
        Console.WriteLine($"   CH02: {_ch02Packets.Count}");

        // ── Detección de clave pública ──
        Console.WriteLine($"\n🔑 Detección de clave pública EC:");
        bool foundPubKey = false;
        var allPackets = _ch04Packets.Concat(_ch02Packets).ToList();
        foreach (var pkt in allPackets)
        {
            if (LooksLikePublicKey(pkt.Data))
            {
                foundPubKey = true;
                Console.WriteLine($"   ✅ ¡CLAVE PÚBLICA DETECTADA! ({pkt.Data.Length}B)");
                Console.WriteLine($"   {BitConverter.ToString(pkt.Data.Take(16).ToArray()).Replace("-", "")}...");
                Console.WriteLine("   🎯 ¡El Unlock + ECDH funciona! El dispositivo entregó su clave pública.");
                break;
            }
        }
        if (!foundPubKey)
        {
            Console.WriteLine("   ❌ No se detectó clave pública en ninguna respuesta.");
        }

        // ── Detección de 0x37 ──
        Console.WriteLine($"\n🔍 Detección de opcode 0x37:");
        if (_detected0x37)
            Console.WriteLine($"   ✅ DETECTADO en {_detected0x37Channel} — protocolo de botones activo.");
        else
            Console.WriteLine("   ❌ No detectado.");

        // ── Detección de paquete misterioso de 85 bytes ──
        Console.WriteLine($"\n📦 Detección de paquete misterioso (85B, FF 03...):");
        bool found85B = false;
        foreach (var pkt in allPackets)
        {
            if (pkt.Data.Length == 85 && pkt.Data.Length >= 5 &&
                pkt.Data[0] == 0xFF && pkt.Data[1] == 0x03 && pkt.Data[2] == 0x00)
            {
                found85B = true;
                Console.WriteLine($"   ✅ Paquete de 85 bytes detectado: {BitConverter.ToString(pkt.Data.Take(20).ToArray()).Replace("-", " ")}...");
                break;
            }
        }
        if (!found85B)
            Console.WriteLine("   ❌ No detectado.");

        // ── Detección de códigos de error/rechazo ──
        Console.WriteLine($"\n🚫 Códigos de error/rechazo:");
        bool found58_02 = false;
        bool found10_64 = false;
        bool foundC0_03 = false;

        foreach (var pkt in allPackets)
        {
            byte[] d = pkt.Data;

            // Buscar "RideOn" + 02 03 + 58 02 (error auth)
            if (d.Length >= 10 &&
                d[0] == 'R' && d[1] == 'i' && d[2] == 'd' &&
                d[3] == 'e' && d[4] == 'O' && d[5] == 'n' &&
                d[6] == 0x02 && d[7] == 0x03 && d[8] == 0x58 && d[9] == 0x02)
            {
                found58_02 = true;
            }

            // Buscar "RideOn" + 02 03 + ... + 10 64 (batería 100%)
            if (d.Length >= 10 &&
                d[0] == 'R' && d[1] == 'i' && d[2] == 'd' &&
                d[3] == 'e' && d[4] == 'O' && d[5] == 'n')
            {
                for (int i = 8; i < d.Length - 1; i++)
                {
                    if (d[i] == 0x10 && d[i + 1] >= 0x50 && d[i + 1] <= 0x64)
                        found10_64 = true;
                }
            }

            // Buscar C0 03 (field 24 = 3, error formato)
            if (d.Length >= 10 &&
                d[0] == 'R' && d[1] == 'i' && d[2] == 'd' &&
                d[3] == 'e' && d[4] == 'O' && d[5] == 'n')
            {
                for (int i = 8; i < d.Length - 1; i++)
                {
                    if (d[i] == 0xC0 && d[i + 1] == 0x03)
                        foundC0_03 = true;
                }
            }
        }

        Console.WriteLine($"   58 02 (error auth):        {(found58_02 ? "⚠️ DETECTADO — handshake rechazado" : "✅ No detectado")}");
        Console.WriteLine($"   10 64 (batería/estado):     {(found10_64 ? "⚠️ DETECTADO — respuesta de estado, no EC key" : "✅ No detectado")}");
        Console.WriteLine($"   C0 03 (error formato):     {(foundC0_03 ? "⚠️ DETECTADO — formato de handshake incorrecto" : "✅ No detectado")}");

        // ── Resumen y recomendación ──
        Console.WriteLine($"\n📋 CONCLUSIÓN:");
        if (foundPubKey)
        {
            Console.WriteLine("   ✅ ÉXITO: El Unlock (FF 04 00) + ECDH funciona.");
            Console.WriteLine("   ➡️  Proceder a derivar clave AES-GCM e integrar el bridge completo.");
        }
        else if (_detected0x37)
        {
            Console.WriteLine("   ⚠️  PARCIAL: Se detectaron eventos de botones (0x37) sin clave pública.");
            Console.WriteLine("   ➡️  El dispositivo está enviando datos pero el handshake ECDH no se completó.");
            Console.WriteLine("   ➡️  Probar otras variantes de payload (ej: pubkey con prefijo 0x04 = 65B).");
        }
        else if (found58_02 || foundC0_03)
        {
            Console.WriteLine("   ❌ RECHAZADO: El dispositivo rechaza el handshake incluso tras Unlock.");
            Console.WriteLine("   ➡️  Posibles causas:");
            Console.WriteLine("   ➡️  - El daily unlock no está activo (conectar con Zwift oficial 30s)");
            Console.WriteLine("   ➡️  - El formato de payload no es el correcto (probar con --fuzz-v2)");
            Console.WriteLine("   ➡️  - El firmware requiere un header binario de 7 bytes en lugar de 'RideOn'");
        }
        else if (found10_64)
        {
            Console.WriteLine("   ❌ ESTADO: El dispositivo responde con datos de batería, no con EC key.");
            Console.WriteLine("   ➡️  El handshake no está siendo procesado como tal.");
            Console.WriteLine("   ➡️  Probar con --probe para explorar todos los formatos.");
        }
        else
        {
            Console.WriteLine("   ❓ INCONCLUSO: No se recibieron suficientes datos para diagnosticar.");
            Console.WriteLine("   ➡️  Verificar que el dispositivo esté encendido y en rango.");
        }

        Console.WriteLine("\n🏁 Advanced Bridge Test completado.");
    }

    /// <summary>
    /// Detecta si un byte array parece una clave pública EC P-256.
    /// Criterios:
    ///   - 65 bytes con prefijo 0x04
    ///   - Bytes 1-64 tienen entropía > 4 bits/byte (no son zeros ni ASCII)
    /// </summary>
    private static bool LooksLikePublicKey(byte[] data)
    {
        if (data == null || data.Length != 65 || data[0] != 0x04)
            return false;

        // Verificar entropía: al menos 40 de los 64 bytes deben ser > 0x1F (no ASCII control)
        int highEntropyCount = 0;
        for (int i = 1; i < 65; i++)
        {
            if (data[i] > 0x1F && data[i] < 0x7F)
                highEntropyCount++;
        }
        // Una clave EC real tiene distribución uniforme (~50% bytes > 0x7F, ~50% < 0x80)
        // Un string ASCII como "2844 mV" tendría casi todos los bytes entre 0x20-0x7E
        // Usamos un umbral: si más del 80% son ASCII imprimible, probablemente no es EC key
        if (highEntropyCount > 51) // > 80% de 64
            return false; // Demasiado ASCII, probablemente texto

        // Verificar que no sean todos ceros
        bool allZero = true;
        for (int i = 1; i < 65 && allZero; i++)
            if (data[i] != 0) allZero = false;
        if (allZero) return false;

        return true;
    }

    private static async Task WriteNoResponseAsync(GattCharacteristic ch, byte[] data)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(data);
        var result = await ch.WriteValueAsync(
            writer.DetachBuffer(),
            GattWriteOption.WriteWithoutResponse);
        if (result != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"WriteWithoutResponse failed: {result}");
    }

    private record CapturedPacket(DateTime Timestamp, byte[] Data);
}