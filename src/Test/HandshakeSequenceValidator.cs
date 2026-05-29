using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using ZwiftClickV2.Bridge.BLE;

namespace ZwiftClickV2.Bridge.Test;

/// <summary>
/// Validador de secuencia de handshake para Zwift Click V2.
/// Replica la secuencia exacta encontrada en repositorios funcionales
/// (qdomyos-zwift, BikeControl) para verificar:
///   - Si el dispositivo responde con clave pública EC o con datos de estado
///   - Si el opcode 0x37 aparece en notificaciones
///   - Qué canales (CH02, CH102, CH04) emiten datos tras RideOn + Unlock
/// </summary>
public static class HandshakeSequenceValidator
{
    // UUIDs de características dentro del servicio 0xFC82
    private static readonly Guid CH02_UUID = BleDeviceManager.CH02_UUID;   // 0x0002 - Async notifications
    private static readonly Guid CH03_UUID = BleDeviceManager.CH03_UUID;   // 0x0003 - SyncRx (write)
    private static readonly Guid CH04_UUID = BleDeviceManager.CH04_UUID;   // 0x0004 - SyncTx (indicate)
    private static readonly Guid CH102_UUID = BleDeviceManager.CH102_UUID; // 0x0102 - Broadcast

    public static async Task RunAsync(GattDeviceService service)
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  Handshake Sequence Validator");
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

        var asyncChar  = chars.FirstOrDefault(c => c.Uuid == CH02_UUID);
        var syncRxChar = chars.FirstOrDefault(c => c.Uuid == CH03_UUID);
        var syncTxChar = chars.FirstOrDefault(c => c.Uuid == CH04_UUID);
        var ch102Char  = chars.FirstOrDefault(c => c.Uuid == CH102_UUID);

        Console.WriteLine($"\nCH02 (Async/0x0002): {(asyncChar != null ? "✅" : "❌")}");
        Console.WriteLine($"CH03 (SyncRx/0x0003): {(syncRxChar != null ? "✅" : "❌")}");
        Console.WriteLine($"CH04 (SyncTx/0x0004): {(syncTxChar != null ? "✅" : "❌")}");
        Console.WriteLine($"CH102 (0x0102):      {(ch102Char != null ? "✅" : "❌")}");

        if (syncRxChar == null || syncTxChar == null)
        {
            Console.WriteLine("\n❌ Características 0x0003 (SyncRx) y/o 0x0004 (SyncTx) no encontradas.");
            Console.WriteLine("   El protocolo requiere ambas para el handshake.");
            return;
        }

        // ── Flags de detección ──
        bool detected0x37 = false;
        bool detected0x37Channel = false; // true = CH02, false = CH102
        string lastRxHex = "(ninguna)";

        // ── Paso 2: Suscribir a SyncTx (CH04) con Indicate ──
        Console.WriteLine("\n🔹 Paso 2: Suscribiendo a SyncTx (CH04) con Indicate...");
        try
        {
            var cccdResult = await syncTxChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Indicate);
            Console.WriteLine($"   {(cccdResult == GattCommunicationStatus.Success ? "✅" : "⚠️")} CCCD Indicate: {cccdResult}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Error suscribiendo: {ex.Message}");
            return;
        }

        byte[] lastSyncTxData = Array.Empty<byte>();
        syncTxChar.ValueChanged += (s, e) =>
        {
            var reader = DataReader.FromBuffer(e.CharacteristicValue);
            lastSyncTxData = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(lastSyncTxData);
            Console.WriteLine($"📨 [SyncTx/CH04] {lastSyncTxData.Length}B: {BitConverter.ToString(lastSyncTxData).Replace("-", " ")}");

            if (lastSyncTxData.Length > 0 && lastSyncTxData[0] == 0x37)
            {
                detected0x37 = true;
                detected0x37Channel = false;
                Console.WriteLine("   ⚡ ¡DETECTADO OPCODE 0x37 en CH04!");
            }
        };

        // ── Paso 3: Enviar "RideOn" ──
        Console.WriteLine("\n🔹 Paso 3: Enviando 'RideOn'...");
        byte[] rideOnBytes = new byte[] { 0x52, 0x69, 0x64, 0x65, 0x4F, 0x6E };
        try
        {
            await WriteNoResponseAsync(syncRxChar, rideOnBytes);
            Console.WriteLine($"   ✅ Write sin respuesta enviado ({rideOnBytes.Length}B): RideOn");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Error: {ex.Message}");
        }
        await Task.Delay(500);
        lastRxHex = lastSyncTxData.Length > 0
            ? BitConverter.ToString(lastSyncTxData).Replace("-", " ")
            : "(sin respuesta)";
        Console.WriteLine($"   📥 Respuesta tras RideOn: {lastRxHex}");

        // ── Paso 5: Enviar Unlock (FF 04 00) ──
        Console.WriteLine("\n🔹 Paso 5: Enviando Unlock (FF 04 00)...");
        byte[] unlockBytes = new byte[] { 0xFF, 0x04, 0x00 };
        try
        {
            await WriteNoResponseAsync(syncRxChar, unlockBytes);
            Console.WriteLine($"   ✅ Write sin respuesta enviado ({unlockBytes.Length}B): FF 04 00");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Error: {ex.Message}");
        }
        await Task.Delay(500);
        lastRxHex = lastSyncTxData.Length > 0
            ? BitConverter.ToString(lastSyncTxData).Replace("-", " ")
            : "(sin respuesta)";
        Console.WriteLine($"   📥 Respuesta tras Unlock: {lastRxHex}");

        // ── Paso 6: Suscribir notificaciones en CH02 y CH102 ──
        Console.WriteLine("\n🔹 Paso 6: Suscribiendo notificaciones en CH02 y CH102...");

        if (asyncChar != null)
        {
            try
            {
                await asyncChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                Console.WriteLine("   ✅ CH02 suscrito (Notify)");

                asyncChar.ValueChanged += (s, e) =>
                {
                    var r = DataReader.FromBuffer(e.CharacteristicValue);
                    var d = new byte[r.UnconsumedBufferLength];
                    r.ReadBytes(d);
                    string hex = BitConverter.ToString(d).Replace("-", " ");
                    Console.WriteLine($"🔔 [CH02/Async] {d.Length}B: {hex}");
                    if (d.Length > 0 && d[0] == 0x37)
                    {
                        detected0x37 = true;
                        detected0x37Channel = true;
                        Console.WriteLine("   ✅ ¡DETECTADO OPCODE 0x37 en CH02! El protocolo coincide.");
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
            Console.WriteLine("   ⚠️ CH02 no disponible, omitiendo.");
        }

        if (ch102Char != null)
        {
            try
            {
                await ch102Char.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                Console.WriteLine("   ✅ CH102 suscrito (Notify)");

                ch102Char.ValueChanged += (s, e) =>
                {
                    var r = DataReader.FromBuffer(e.CharacteristicValue);
                    var d = new byte[r.UnconsumedBufferLength];
                    r.ReadBytes(d);
                    string hex = BitConverter.ToString(d).Replace("-", " ");
                    Console.WriteLine($"🔔 [CH102] {d.Length}B: {hex}");
                    if (d.Length > 0 && d[0] == 0x37)
                    {
                        detected0x37 = true;
                        detected0x37Channel = false;
                        Console.WriteLine("   ✅ ¡DETECTADO OPCODE 0x37 en CH102!");
                    }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Error CH102: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("   ⚠️ CH102 no disponible, omitiendo.");
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
        Console.WriteLine("  RESULTADOS DE VALIDACIÓN");
        Console.WriteLine("═══════════════════════════════════════════");

        Console.WriteLine($"\n📋 Respuesta tras RideOn + Unlock:");
        Console.WriteLine($"   {lastRxHex}");

        Console.WriteLine($"\n🔍 Detección de opcode 0x37:");
        if (detected0x37)
        {
            string channel = detected0x37Channel ? "CH02 (Async)" : "CH04/CH102";
            Console.WriteLine($"   ✅ DETECTADO en {channel}");
            Console.WriteLine("   🎯 ¡El protocolo de qdomyos-zwift / BikeControl coincide con tu dispositivo!");
            Console.WriteLine("   ➡️ Procede a integrar ZwiftClickV2BleManager.cs y mapear botones.");
        }
        else
        {
            Console.WriteLine("   ❌ NO detectado en ningún canal.");
            Console.WriteLine("   Posibles causas:");
            Console.WriteLine("   - El dispositivo requiere un token diario previo (conectar con Zwift oficial 30s)");
            Console.WriteLine("   - El formato Unlock (FF 04 00) no es el correcto para esta versión de firmware");
            Console.WriteLine("   - El opcode 0x37 solo aparece tras handshake ECDH exitoso, no tras RideOn crudo");
            Console.WriteLine("   Sugerencia: Prueba con --probe para ver todos los formatos de handshake.");
        }

        Console.WriteLine("\n🏁 Validación completada.");
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
}