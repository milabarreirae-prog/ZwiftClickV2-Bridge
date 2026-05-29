using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using ZwiftClickV2.Bridge.BLE;
using ZwiftClickV2.Bridge.Crypto;
using ZwiftClickV2.Bridge.Protocol;
using ZwiftClickV2.Bridge.Protocol.Messages;

namespace ZwiftClickV2.Bridge.Bridge;

/// <summary>
/// Prueba el comando "Unlock" reportado por la comunidad: FF 04 00.
/// 
/// Hipótesis: Zwift oficial envía FF 04 00 a CH03 (o CH06/CH100)
/// para resetear el temporizador de DRM de 24 horas del firmware.
/// 
/// El prober intenta:
/// 1. Conectar al dispositivo
/// 2. Enviar FF 04 00 a CH03 como WriteWithoutResponse
/// 3. Enviar FF 04 00 a CH06 como WriteWithoutResponse (si existe)
/// 4. Intentar handshake ECDH normal
/// 5. Comparar si la respuesta cambió (de rechazo → clave pública)
/// 
/// Variantes probadas:
/// - FF 04 00 solo
/// - FF 04 00 antes del handshake
/// - FF 04 00 en CH06 (canal de info/estado)
/// - FF 04 00 en CH100 (canal de control primario V2)
/// </summary>
public class UnlockProber
{
    private readonly BleDeviceManager _ble = new();
    private readonly BleNotificationListener _listener = new();

    public async Task<int> RunAsync(string deviceName = "Zwift Click")
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  UNLOCK PROBER — Zwift Click V2");
        Console.WriteLine("  Probando comando FF 04 00 para desbloquear DRM");
        Console.WriteLine("═══════════════════════════════════════════\n");

        // ── 1. Conectar ─────────────────────────────────────────────────
        var device = await _ble.ConnectAsync(deviceName);
        if (device == null) return 1;

        // ── 2. Obtener características ──────────────────────────────────
        var service = await _ble.GetServiceAsync(BleDeviceManager.ZWIFT_SERVICE_UUID);
        if (service == null)
        {
            Console.WriteLine("❌ Servicio Zwift no encontrado");
            return 1;
        }

        var charsResult = await service.GetCharacteristicsAsync();
        var allChars = charsResult.Characteristics;

        var ch03 = allChars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH03_UUID);
        var ch04 = allChars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH04_UUID);
        var ch06 = allChars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH06_UUID);
        var ch100 = allChars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH100_UUID);

        if (ch03 == null) { Console.WriteLine("❌ CH03 no encontrado"); return 1; }
        if (ch04 == null) { Console.WriteLine("❌ CH04 no encontrado"); return 1; }

        Console.WriteLine($"CH03 (SyncTx): ✅");
        Console.WriteLine($"CH04 (SyncRx): ✅");
        Console.WriteLine($"CH06 (Info):   {(ch06 != null ? "✅" : "❌")}");
        Console.WriteLine($"CH100 (Ctrl):  {(ch100 != null ? "✅" : "❌")}");

        // ── 3. Handshake de referencia (sin unlock, para comparar) ──────
        Console.WriteLine("\n─── FASE 1: Handshake SIN unlock previo ───");
        byte[]? baselineResponse = await DoHandshakeAsync(ch03, ch04);
        bool baselineIsRejection = baselineResponse != null && IsRejection(baselineResponse);

        if (baselineResponse == null)
        {
            Console.WriteLine("⚠️  Sin respuesta en handshake base. Continuando con pruebas...");
        }
        else
        {
            Console.WriteLine($"   Baseline response ({baselineResponse.Length}B): {(baselineIsRejection ? "RECHAZO 🔒" : "ÉXITO 🎉")}");
        }

        // ── 4. Probar variantes de unlock ───────────────────────────────
        var unlockVariants = new[]
        {
            (name: "FF 04 00 → CH03 (WriteWithoutResponse)", channel: ch03, payload: new byte[] { 0xFF, 0x04, 0x00 }),
            (name: "FF 04 00 00 → CH03 (WriteWithoutResponse)", channel: ch03, payload: new byte[] { 0xFF, 0x04, 0x00, 0x00 }),
            (name: "FF 04 00 00 00 → CH03 (WriteWithoutResponse)", channel: ch03, payload: new byte[] { 0xFF, 0x04, 0x00, 0x00, 0x00 }),
        };

        // Añadir CH06 y CH100 si existen
        if (ch06 != null)
        {
            unlockVariants = unlockVariants.Append(
                (name: "FF 04 00 → CH06 (WriteWithoutResponse)", channel: ch06, payload: new byte[] { 0xFF, 0x04, 0x00 })
            ).ToArray();
        }
        if (ch100 != null)
        {
            unlockVariants = unlockVariants.Append(
                (name: "FF 04 00 → CH100 (WriteWithoutResponse)", channel: ch100, payload: new byte[] { 0xFF, 0x04, 0x00 })
            ).ToArray();
        }

        foreach (var (name, channel, payload) in unlockVariants)
        {
            Console.WriteLine($"\n─── Probando: {name} ───");

            try
            {
                var writer = new DataWriter();
                writer.WriteBytes(payload);
                var status = await channel.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
                Console.WriteLine($"   Write status: {status}");

                // Pequeña pausa para que el firmware procese
                await Task.Delay(300);

                // Intentar handshake
                byte[]? response = await DoHandshakeAsync(ch03, ch04);
                if (response == null)
                {
                    Console.WriteLine("   ❌ Sin respuesta al handshake");
                    continue;
                }

                bool isRejection = IsRejection(response);
                Console.WriteLine($"   Respuesta ({response.Length}B): {(isRejection ? "Sigue RECHAZANDO 🔒" : "¿ÉXITO? 🎉")}");

                if (!isRejection)
                {
                    Console.WriteLine($"\n   🎯 ¡POSIBLE UNLOCK DETECTADO!");
                    Console.WriteLine($"   Comando: {name}");
                    Console.WriteLine($"   Payload: {BitConverter.ToString(payload).Replace("-", " ")}");
                    Console.WriteLine($"   Respuesta: {BitConverter.ToString(response).Replace("-", "")}");

                    // Verificar si contiene clave pública
                    var pubKey = HandshakeParser.ExtractPublicKey(response);
                    if (pubKey != null)
                    {
                        Console.WriteLine($"   ✅ Clave pública EC detectada (65B)");
                        return 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Error: {ex.Message}");
            }
        }

        // ── 5. Resumen ──────────────────────────────────────────────────
        Console.WriteLine("\n═══════════════════════════════════════════");
        Console.WriteLine("  RESULTADO: Comando FF 04 00 NO desbloqueó el dispositivo");
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine("Posibles explicaciones:");
        Console.WriteLine("  - El comando unlock usa bytes diferentes");
        Console.WriteLine("  - El unlock requiere una secuencia más larga");
        Console.WriteLine("  - El unlock se envía a otra característica");
        Console.WriteLine("  - El unlock requiere estar paired/bonded");
        Console.WriteLine("  - El DRM está en hardware y no se puede bypass vía BLE");
        Console.WriteLine();
        Console.WriteLine("Recomendación: Usa el workflow 'Hot Pairing'");
        Console.WriteLine("  1. Abre Zwift oficial, conecta el Click 30s");
        Console.WriteLine("  2. Cierra Zwift");
        Console.WriteLine("  3. Ejecuta: ZwiftClickV2-Bridge.exe --hot-pair");
        return 1;
    }

    /// <summary>
    /// Realiza un handshake ECDH simple y retorna la respuesta cruda.
    /// </summary>
    private async Task<byte[]?> DoHandshakeAsync(GattCharacteristic ch03, GattCharacteristic ch04)
    {
        // Generar par ECDH
        using var ourKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ourParams = ourKey.ExportParameters(false);
        byte[] ourPubKey65 = new byte[65];
        ourPubKey65[0] = 0x04;
        Array.Copy(ourParams.Q.X!, 0, ourPubKey65, 1, 32);
        Array.Copy(ourParams.Q.Y!, 0, ourPubKey65, 33, 32);

        var hello = new ZopHello { PublicKey = ourPubKey65, Suffix = new byte[] { 0x01, 0x02 } };
        byte[] payload = hello.BuildHandshakePayload();

        var responseTcs = new TaskCompletionSource<byte[]>();

        // Suscribir CH04
        await _listener.SubscribeAsync(BleDeviceManager.CH04_UUID, ch04, data =>
        {
            responseTcs.TrySetResult(data);
        }, useIndicate: true);

        // Enviar handshake
        var writer = new DataWriter();
        writer.WriteBytes(payload);
        await ch03.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);

        // Esperar respuesta
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await responseTcs.Task.WaitAsync(cts.Token);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Determina si una respuesta es un rechazo (no contiene clave pública EC).
    /// </summary>
    private static bool IsRejection(byte[] response)
    {
        if (response == null || response.Length < 8) return true;

        // ¿Contiene clave pública 0x04 + 64 bytes?
        if (HandshakeParser.ExtractPublicKey(response) != null)
            return false;

        return true;
    }
}