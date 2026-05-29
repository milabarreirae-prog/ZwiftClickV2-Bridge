using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using ZwiftClickV2.Bridge.BLE;

namespace ZwiftClickV2.Bridge.Test;

/// <summary>
/// Validador de secuencia de handshake V2 para Zwift Click V2.
/// Apunta a las características nativas V2 (CH100, CH101, CH102)
/// dentro del servicio 0xFC82, replicando la secuencia encontrada
/// en qdomyos-zwift y BikeControl:
///
///   1. Suscribir CH101 (Indicate) y CH102 (Notify)
///   2. Enviar "RideOn" por CH100
///   3. Esperar respuesta
///   4. Enviar Unlock (FF 04 00) por CH100
///   5. Escuchar notificaciones 8s y detectar opcode 0x37
///   6. También suscribir CH02 para eventos de botones
/// </summary>
public static class HandshakeSequenceValidatorV2
{
    private static readonly Guid CH02_UUID  = BleDeviceManager.CH02_UUID;   // 0x0002 - Async
    private static readonly Guid CH100_UUID = BleDeviceManager.CH100_UUID;  // 0x0100 - Control primario
    private static readonly Guid CH101_UUID = BleDeviceManager.CH101_UUID;  // 0x0101 - Control secundario
    private static readonly Guid CH102_UUID = BleDeviceManager.CH102_UUID;  // 0x0102 - Broadcast

    public static async Task RunAsync(GattDeviceService service)
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  Handshake Sequence Validator V2");
        Console.WriteLine("  (CH100/CH101/CH102 — qdomyos-zwift path)");
        Console.WriteLine("═══════════════════════════════════════════\n");

        // ── Resolver características ──
        var characteristics = await service.GetCharacteristicsAsync();
        if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics == null)
        {
            Console.WriteLine("❌ No se pudieron obtener características del servicio.");
            return;
        }

        var chars = characteristics.Characteristics.ToList();
        Console.WriteLine($"Características encontradas: {chars.Count}");
        foreach (var c in chars)
            Console.WriteLine($"   {c.Uuid} — Properties: {c.CharacteristicProperties}");

        var ch02Char  = chars.FirstOrDefault(c => c.Uuid == CH02_UUID);
        var ch100Char = chars.FirstOrDefault(c => c.Uuid == CH100_UUID);
        var ch101Char = chars.FirstOrDefault(c => c.Uuid == CH101_UUID);
        var ch102Char = chars.FirstOrDefault(c => c.Uuid == CH102_UUID);

        Console.WriteLine($"\nCH02  (Async/0x0002): {(ch02Char != null ? "✅" : "❌")}");
        Console.WriteLine($"CH100 (Ctrl/0x0100):  {(ch100Char != null ? "✅" : "❌")}");
        Console.WriteLine($"CH101 (Ctrl/0x0101):  {(ch101Char != null ? "✅" : "❌")}");
        Console.WriteLine($"CH102 (Bcast/0x0102): {(ch102Char != null ? "✅" : "❌")}");

        if (ch100Char == null)
        {
            Console.WriteLine("\n❌ CH100 (0x0100) no encontrada. Sin esta característica no se puede probar el handshake V2.");
            return;
        }

        // Mostrar propiedades de CH100 (importante para saber si acepta Write o solo WriteWithoutResponse)
        Console.WriteLine($"\n📋 Propiedades de CH100: {ch100Char.CharacteristicProperties}");
        bool ch100SupportsWrite = ch100Char.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
        bool ch100SupportsWriteNoResponse = ch100Char.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        Console.WriteLine($"   Write: {(ch100SupportsWrite ? "✅" : "❌")}  WriteWithoutResponse: {(ch100SupportsWriteNoResponse ? "✅" : "❌")}");

        // ── Flags de detección ──
        bool detected0x37 = false;
        string detected0x37Channel = "";

        // ── Paso 1: Suscribir CH101 con Indicate (si existe) ──
        if (ch101Char != null)
        {
            Console.WriteLine("\n🔹 Paso 1: Suscribiendo CH101 (0x0101) con Indicate...");
            try
            {
                var cccdResult = await ch101Char.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Indicate);
                Console.WriteLine($"   {(cccdResult == GattCommunicationStatus.Success ? "✅" : "⚠️")} CCCD Indicate: {cccdResult}");

                ch101Char.ValueChanged += (s, e) =>
                {
                    var r = DataReader.FromBuffer(e.CharacteristicValue);
                    var d = new byte[r.UnconsumedBufferLength];
                    r.ReadBytes(d);
                    string hex = BitConverter.ToString(d).Replace("-", " ");
                    Console.WriteLine($"📨 [CH101/0x0101] {d.Length}B: {hex}");
                    if (d.Length > 0 && d[0] == 0x37)
                    {
                        detected0x37 = true;
                        detected0x37Channel = "CH101";
                        Console.WriteLine("   ⚡ ¡DETECTADO OPCODE 0x37 en CH101!");
                    }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Error CH101: {ex.Message}");
            }
        }

        // ── Paso 2: Suscribir CH102 con Notify ──
        if (ch102Char != null)
        {
            Console.WriteLine("\n🔹 Paso 2: Suscribiendo CH102 (0x0102) con Notify...");
            try
            {
                var cccdResult = await ch102Char.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                Console.WriteLine($"   {(cccdResult == GattCommunicationStatus.Success ? "✅" : "⚠️")} CCCD Notify: {cccdResult}");

                ch102Char.ValueChanged += (s, e) =>
                {
                    var r = DataReader.FromBuffer(e.CharacteristicValue);
                    var d = new byte[r.UnconsumedBufferLength];
                    r.ReadBytes(d);
                    string hex = BitConverter.ToString(d).Replace("-", " ");
                    Console.WriteLine($"🔔 [CH102/0x0102] {d.Length}B: {hex}");
                    if (d.Length > 0 && d[0] == 0x37)
                    {
                        detected0x37 = true;
                        detected0x37Channel = "CH102";
                        Console.WriteLine("   ⚡ ¡DETECTADO OPCODE 0x37 en CH102!");
                    }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Error CH102: {ex.Message}");
            }
        }

        // ── Buffer para capturar respuesta de CH100 si tiene Notify/Indicate ──
        if (ch100Char.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) ||
            ch100Char.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
        {
            Console.WriteLine("\n🔹 Suscribiendo CH100 (0x0100) para respuestas...");
            try
            {
                var value = ch100Char.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)
                    ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                    : GattClientCharacteristicConfigurationDescriptorValue.Notify;
                await ch100Char.WriteClientCharacteristicConfigurationDescriptorAsync(value);
                Console.WriteLine("   ✅ CH100 suscrito");

                ch100Char.ValueChanged += (s, e) =>
                {
                    var r = DataReader.FromBuffer(e.CharacteristicValue);
                    var d = new byte[r.UnconsumedBufferLength];
                    r.ReadBytes(d);
                    string hex = BitConverter.ToString(d).Replace("-", " ");
                    Console.WriteLine($"📨 [CH100/0x0100] {d.Length}B: {hex}");
                    if (d.Length > 0 && d[0] == 0x37)
                    {
                        detected0x37 = true;
                        detected0x37Channel = "CH100";
                        Console.WriteLine("   ⚡ ¡DETECTADO OPCODE 0x37 en CH100!");
                    }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️ No se pudo suscribir CH100: {ex.Message}");
            }
        }

        // ── Paso 3: Enviar "RideOn" por CH100 ──
        Console.WriteLine("\n🔹 Paso 3: Enviando 'RideOn' por CH100...");
        byte[] rideOnBytes = new byte[] { 0x52, 0x69, 0x64, 0x65, 0x4F, 0x6E };
        await WriteToCharAsync(ch100Char, rideOnBytes, "RideOn");
        await Task.Delay(500);

        // ── Paso 4: Enviar Unlock (FF 04 00) por CH100 ──
        Console.WriteLine("\n🔹 Paso 4: Enviando Unlock (FF 04 00) por CH100...");
        byte[] unlockBytes = new byte[] { 0xFF, 0x04, 0x00 };
        await WriteToCharAsync(ch100Char, unlockBytes, "FF 04 00");
        await Task.Delay(500);

        // ── Paso 5: Suscribir CH02 para eventos de botones ──
        if (ch02Char != null)
        {
            Console.WriteLine("\n🔹 Paso 5: Suscribiendo CH02 (0x0002) para eventos de botones...");
            try
            {
                await ch02Char.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                Console.WriteLine("   ✅ CH02 suscrito (Notify)");

                ch02Char.ValueChanged += (s, e) =>
                {
                    var r = DataReader.FromBuffer(e.CharacteristicValue);
                    var d = new byte[r.UnconsumedBufferLength];
                    r.ReadBytes(d);
                    string hex = BitConverter.ToString(d).Replace("-", " ");
                    Console.WriteLine($"🎮 [CH02/Botones] {d.Length}B: {hex}");
                    if (d.Length > 0 && d[0] == 0x37)
                    {
                        detected0x37 = true;
                        detected0x37Channel = "CH02";
                        Console.WriteLine("   ✅ ¡DETECTADO OPCODE 0x37 en CH02! El protocolo coincide.");
                    }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Error CH02: {ex.Message}");
            }
        }

        // ── Esperar y capturar ──
        Console.WriteLine("\n⏳ Esperando 8 segundos para capturar notificaciones...");
        Console.WriteLine("   (presiona botones del Click V2 si quieres ver eventos)\n");

        for (int i = 8; i > 0; i--)
        {
            Console.Write($"\r   ⏱️  {i}s restantes...  ");
            await Task.Delay(1000);
        }
        Console.WriteLine();

        // ── Resumen ──
        Console.WriteLine("\n═══════════════════════════════════════════");
        Console.WriteLine("  RESULTADOS DE VALIDACIÓN V2");
        Console.WriteLine("═══════════════════════════════════════════");

        Console.WriteLine($"\n🔍 Detección de opcode 0x37:");
        if (detected0x37)
        {
            Console.WriteLine($"   ✅ DETECTADO en {detected0x37Channel}");
            Console.WriteLine("   🎯 ¡El protocolo de qdomyos-zwift / BikeControl coincide con tu dispositivo!");
            Console.WriteLine("   ➡️ Procede a integrar ZwiftClickV2BleManager.cs y mapear botones.");
        }
        else
        {
            Console.WriteLine("   ❌ NO detectado en ningún canal (CH100, CH101, CH102, CH02).");
            Console.WriteLine();
            Console.WriteLine("   Hipótesis a verificar:");
            Console.WriteLine("   1. ¿El dispositivo tiene el 'daily unlock' activo?");
            Console.WriteLine("      → Conéctalo a Zwift oficial 30s y vuelve a probar.");
            Console.WriteLine("   2. ¿CH100 acepta Write en lugar de WriteWithoutResponse?");
            Console.WriteLine($"      → Propiedades detectadas: {ch100Char.CharacteristicProperties}");
            Console.WriteLine("   3. ¿El servicio legacy (00000001-...) está también activo?");
            Console.WriteLine("      → Prueba --validate para el camino legacy (CH03/CH04).");
            Console.WriteLine("   4. ¿El firmware requiere un header binario de 7 bytes en lugar de 'RideOn'?");
            Console.WriteLine("      → Prueba --fuzz-v2 para descubrir el header correcto.");
        }

        // ── Sugerir si el Write fue con respuesta ──
        if (!ch100SupportsWriteNoResponse && ch100SupportsWrite)
        {
            Console.WriteLine("\n⚠️  CH100 solo soporta Write (con respuesta), no WriteWithoutResponse.");
            Console.WriteLine("   Si el firmware espera WriteWithoutResponse, este test podría fallar.");
            Console.WriteLine("   Sugerencia: forzar WriteWithoutResponse e ignorar el error GATT.");
        }

        Console.WriteLine("\n🏁 Validación V2 completada.");
    }

    /// <summary>
    /// Intenta WriteWithoutResponse primero; si falla, intenta Write.
    /// </summary>
    private static async Task WriteToCharAsync(GattCharacteristic ch, byte[] data, string label)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(data);
        var buffer = writer.DetachBuffer();

        // Intentar WriteWithoutResponse primero (lo que usa qdomyos-zwift)
        if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
        {
            try
            {
                var result = await ch.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
                Console.WriteLine($"   ✅ WriteWithoutResponse ({data.Length}B): {label} → {result}");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️ WriteWithoutResponse falló: {ex.Message}");
            }
        }

        // Fallback: Write con respuesta
        if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write))
        {
            try
            {
                var result = await ch.WriteValueAsync(buffer, GattWriteOption.WriteWithResponse);
                Console.WriteLine($"   ✅ Write (con respuesta) ({data.Length}B): {label} → {result}");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Write falló: {ex.Message}");
                throw;
            }
        }

        throw new InvalidOperationException($"CH100 no soporta Write ni WriteWithoutResponse. Properties: {ch.CharacteristicProperties}");
    }
}