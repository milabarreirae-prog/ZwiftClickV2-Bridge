using ZwiftClickV2.Bridge.Bridge;
using ZwiftClickV2.Bridge.Logging;
using ZwiftClickV2.Bridge.Protocol;

namespace ZwiftClickV2.Bridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "--bridge":
            case "-b":
                return await RunBridgeAsync(args);

            case "--probe":
            case "-p":
                return await HandshakeProber.RunAsync();

            case "--fuzz":
            case "-f":
                return await RunFuzzerAsync();

            case "--help":
            case "-h":
            case "-?":
                PrintUsage();
                return 0;

            default:
                Console.WriteLine($"❌ Argumento desconocido: {args[0]}");
                PrintUsage();
                return 1;
        }
    }

    private static async Task<int> RunBridgeAsync(string[] args)
    {
        string deviceName = args.Length > 1 ? args[1] : "Zwift Click";
        using var bridge = new ClickV2Bridge();
        bool success = await bridge.StartAsync(deviceName);

        if (success)
        {
            Console.WriteLine("\nPresiona ENTER para detener el bridge...");
            Console.ReadLine();
            bridge.Stop();
            return 0;
        }
        return 1;
    }

    private static async Task<int> RunFuzzerAsync()
    {
        using var logger = new StructuredLogger();
        var fuzzer = new HandshakeFuzzer(logger);
        var decryptor = new PacketDecryptor(logger);

        // 1. Fuzzear handshakes
        var results = await fuzzer.RunAsync();

        // 2. Si algún handshake funcionó, capturar paquetes de CH02 por 10s
        var successResult = results.FirstOrDefault(r => r.IsPublicKey && r.Notes.Contains("ENCRYPTION_OK"));
        if (successResult != null)
        {
            logger.LogInfo("\n📡 Capturando paquetes en CH02 por 10 segundos...");
            await Task.Delay(10000);
            logger.LogInfo($"   Paquetes capturados: {decryptor.CapturedPackets.Count}");
        }

        // 3. Intentar descifrar todos los paquetes capturados
        if (decryptor.CapturedPackets.Count > 0)
        {
            logger.LogInfo("\n🔬 Intentando descifrar paquetes capturados...");
            var decryptResults = await decryptor.DecryptAllAsync();

            int successCount = decryptResults.Count(r => r.Success);
            logger.LogInfo($"\n📊 Descifrado: {successCount}/{decryptResults.Count} exitosos");
        }

        // 4. Resumen final
        logger.LogInfo("\n═══════════════════════════════════════════");
        logger.LogInfo("  FUZZER COMPLETADO");
        logger.LogInfo($"  Variaciones probadas: {results.Count}");
        logger.LogInfo($"  Con clave pública: {results.Count(r => r.IsPublicKey)}");
        logger.LogInfo($"  Con cifrado exitoso: {results.Count(r => r.Notes.Contains("ENCRYPTION_OK"))}");
        logger.LogInfo($"  Paquetes capturados: {decryptor.CapturedPackets.Count}");
        logger.LogInfo($"  Log guardado en: {logger.FilePath}");

        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ZwiftClickV2-Bridge v2.0");
        Console.WriteLine();
        Console.WriteLine("Uso:");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --bridge [deviceName]   Iniciar bridge completo");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --probe                 Probar formatos de handshake");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --fuzz                  Fuzzear handshakes + capturar paquetes");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --help                  Mostrar esta ayuda");
        Console.WriteLine();
        Console.WriteLine("Modos:");
        Console.WriteLine("  --bridge   Conecta al Click V2, hace handshake ECDH,");
        Console.WriteLine("             cifra la sesión, y emula teclas para MyWoosh.");
        Console.WriteLine("  --probe    Itera sobre 6 formatos de handshake y detecta");
        Console.WriteLine("             automáticamente el formato correcto.");
        Console.WriteLine("  --fuzz     Fuzzer exhaustivo (~15 variaciones) con logs");
        Console.WriteLine("             JSON, captura de paquetes y descifrado.");
    }
}