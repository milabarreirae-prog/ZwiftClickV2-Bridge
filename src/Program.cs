using ZwiftClickV2.Bridge.BLE;
using ZwiftClickV2.Bridge.Bridge;
using ZwiftClickV2.Bridge.Logging;
using ZwiftClickV2.Bridge.Protocol;
using ZwiftClickV2.Bridge.Test;

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

            case "--fuzz-v2":
                return await RunFuzzerV2Async();

            case "--validate":
            case "-v":
                return await RunValidatorAsync(args);

            case "--validate-v2":
                return await RunValidatorV2Async(args);

            case "--advanced-bridge":
                return await RunAdvancedBridgeAsync(args);

            case "--hot-pair":
            case "-hp":
                return await RunHotPairAsync(args);

            case "--diagnose-crypto":
                return await RunCryptoDiagnosisAsync(args);

            case "--unlock-probe":
            case "-up":
                return await RunUnlockProbeAsync(args);

            case "--unlock-assist":
            case "-ua":
                return await RunUnlockAssistAsync(args);

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
        string deviceName = GetDeviceNameArg(args);
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

    private static async Task<int> RunFuzzerV2Async()
    {
        using var logger = new StructuredLogger();
        logger.LogInfo("Modo: Fuzzer de Handshake V2 — Header Discovery");

        var fuzzerV2 = new HandshakeFuzzerV2(logger);
        var results = await fuzzerV2.RunAsync();

        var winner = results.FirstOrDefault(r => r.Status == "SUCCESS");
        if (winner != null)
        {
            logger.LogInfo("");
            logger.LogInfo("🎯 PRÓXIMO PASO:");
            logger.LogInfo($"Implementar el handshake con header: {winner.HeaderName}");
            logger.LogInfo($"Bytes: {BitConverter.ToString(winner.HeaderBytes).Replace("-", " ")}");
            logger.LogInfo($"Peer pubkey: {BitConverter.ToString(winner.PeerPublicKey!).Replace("-", " ")}");
        }
        else
        {
            logger.LogInfo("");
            logger.LogInfo("❌ Ningún formato de header funcionó.");
            logger.LogInfo("Sugerencia: Capturar tráfico BLE de Zwift oficial con sniffer hardware");
            logger.LogInfo("   - nRF52840 Dongle (~$15 USD) + Wireshark");
            logger.LogInfo("   - Ubertooth One (~$120 USD)");
        }

        logger.LogInfo($"Log guardado en: {logger.FilePath}");
        return 0;
    }

    private static async Task<int> RunValidatorAsync(string[] args)
    {
        string deviceName = GetDeviceNameArg(args);

        var ble = new BleDeviceManager();
        var device = await ble.ConnectAsync(deviceName);
        if (device == null)
        {
            Console.WriteLine("❌ No se pudo conectar al dispositivo.");
            return 1;
        }

        var service = await ble.GetServiceAsync(BleDeviceManager.ZWIFT_SERVICE_UUID);
        if (service == null)
        {
            Console.WriteLine("❌ Servicio 0xFC82 no encontrado.");
            return 1;
        }

        await HandshakeSequenceValidator.RunAsync(service);
        ble.Disconnect();
        return 0;
    }

    private static async Task<int> RunValidatorV2Async(string[] args)
    {
        string deviceName = GetDeviceNameArg(args);

        var ble = new BleDeviceManager();
        var device = await ble.ConnectAsync(deviceName);
        if (device == null)
        {
            Console.WriteLine("❌ No se pudo conectar al dispositivo.");
            return 1;
        }

        var service = await ble.GetServiceAsync(BleDeviceManager.ZWIFT_SERVICE_UUID);
        if (service == null)
        {
            Console.WriteLine("❌ Servicio 0xFC82 no encontrado.");
            return 1;
        }

        await HandshakeSequenceValidatorV2.RunAsync(service);
        ble.Disconnect();
        return 0;
    }

    private static async Task<int> RunAdvancedBridgeAsync(string[] args)
    {
        string deviceName = GetDeviceNameArg(args);

        var ble = new BleDeviceManager();
        var device = await ble.ConnectAsync(deviceName);
        if (device == null)
        {
            Console.WriteLine("❌ No se pudo conectar al dispositivo.");
            return 1;
        }

        var service = await ble.GetServiceAsync(BleDeviceManager.ZWIFT_SERVICE_UUID);
        if (service == null)
        {
            Console.WriteLine("❌ Servicio 0xFC82 no encontrado.");
            return 1;
        }

        await AdvancedBridgeTest.RunAsync(service);
        ble.Disconnect();
        return 0;
    }

    private static async Task<int> RunHotPairAsync(string[] args)
    {
        string deviceName = GetDeviceNameArg(args);
        bool diagnoseCrypto = args.Any(a => a.Equals("--diagnose-crypto", StringComparison.OrdinalIgnoreCase));
        bool authDryRun = args.Any(a => a.Equals("--auth-dry-run", StringComparison.OrdinalIgnoreCase));
        IAuthChallengeClient? authClient = BuildAuthClientFromArgs(args);

        using var bridge = new HotPairBridge(authClient, authDryRun);
        bool success = await bridge.StartAsync(deviceName, diagnoseCrypto: diagnoseCrypto);

        if (success)
        {
            Console.WriteLine("\nPresiona ENTER para detener el bridge...");
            Console.ReadLine();
            bridge.Stop();
            return 0;
        }
        return 1;
    }

    private static async Task<int> RunCryptoDiagnosisAsync(string[] args)
    {
        string deviceName = GetDeviceNameArg(args);
        bool authDryRun = args.Any(a => a.Equals("--auth-dry-run", StringComparison.OrdinalIgnoreCase));
        IAuthChallengeClient? authClient = BuildAuthClientFromArgs(args);

        using var bridge = new HotPairBridge(authClient, authDryRun);
        bool success = await bridge.StartAsync(deviceName, diagnoseCrypto: true);

        if (success)
        {
            Console.WriteLine("\nPresiona ENTER para detener el bridge...");
            Console.ReadLine();
            bridge.Stop();
            return 0;
        }
        return 1;
    }

    private static async Task<int> RunUnlockProbeAsync(string[] args)
    {
        string deviceName = GetDeviceNameArg(args);
        var prober = new UnlockProber();
        return await prober.RunAsync(deviceName);
    }

    private static async Task<int> RunUnlockAssistAsync(string[] args)
    {
        string deviceName = GetDeviceNameArg(args);
        var assist = new UnlockAssistBridge();
        return await assist.RunAsync(deviceName);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ZwiftClickV2-Bridge v2.0");
        Console.WriteLine();
        Console.WriteLine("Uso:");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --bridge [deviceName]   Iniciar bridge completo");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --hot-pair [deviceName] BikeControl-style ECDH handshake");
        Console.WriteLine("      Opciones hot-pair: --auth-replay-hardcoded | --auth-replay-hex <HEX> | --auth-replay-file <path> | --auth-dry-run");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --diagnose-crypto [deviceName] Hot-pair + diagnóstico cripto V2");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --unlock-assist [deviceName] Asistente interactivo de desbloqueo");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --unlock-probe [deviceName] Probar comportamiento de FF 04 00");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --probe                 Probar formatos de handshake");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --fuzz                  Fuzzear handshakes + capturar paquetes");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --fuzz-v2               Fuzzear headers V2 (10 formatos de 7 bytes)");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --validate [deviceName]  Validar secuencia legacy (CH03/CH04)");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --validate-v2 [deviceName] Validar secuencia V2 (CH100/CH101/CH102)");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --advanced-bridge [deviceName] Test Unlock→ECDH→Captura (3 fases)");
        Console.WriteLine("  ZwiftClickV2-Bridge.exe --help                  Mostrar esta ayuda");
        Console.WriteLine();
        Console.WriteLine("Modos:");
        Console.WriteLine("  --hot-pair  🔥 BikeControl-style: suscribe CH04 PRIMERO,");
        Console.WriteLine("              envía handshake como primer write, sin lecturas previas.");
        Console.WriteLine("              Requiere 'Hot Pairing': abre Zwift, conecta Click 30s,");
        Console.WriteLine("              cierra Zwift, ejecuta este bridge inmediatamente.");
        Console.WriteLine("  --diagnose-crypto Ejecuta hot-pair e imprime HKDF, IV y pruebas AAD.");
        Console.WriteLine("  --auth-replay-hardcoded Usa payload auth de prueba embebido para replay rapido.");
        Console.WriteLine("  --auth-replay-hex  Inyecta payload auth fijo (hex) cuando se detecta challenge.");
        Console.WriteLine("  --auth-replay-file Lee payload auth fijo desde archivo (hex en texto).");
        Console.WriteLine("  --auth-dry-run     No envía respuesta BLE de auth; solo registra telemetría.");
        Console.WriteLine("  --unlock-probe  🔓 Prueba el comando FF 04 00 en CH03/CH06/CH100");
        Console.WriteLine("                  como posible feedback o control secundario.");
        Console.WriteLine("  --bridge    Conecta al Click V2, hace handshake ECDH,");
        Console.WriteLine("              cifra la sesión, y emula teclas para MyWoosh.");
        Console.WriteLine("  --probe     Itera sobre 6 formatos de handshake y detecta");
        Console.WriteLine("              automáticamente el formato correcto.");
        Console.WriteLine("  --fuzz      Fuzzer exhaustivo (~15 variaciones) con logs");
        Console.WriteLine("              JSON, captura de paquetes y descifrado.");
        Console.WriteLine("  --fuzz-v2   Fuzzer de headers V2: prueba 10 headers de 7 bytes");
        Console.WriteLine("              para descubrir el formato correcto del handshake.");
        Console.WriteLine("  --validate    Valida secuencia legacy (CH03/CH04) RideOn+Unlock");
        Console.WriteLine("                contra tu dispositivo. Detecta opcode 0x37.");
        Console.WriteLine("  --validate-v2   Valida secuencia V2 (CH100/CH101/CH102) RideOn+Unlock");
        Console.WriteLine("                  contra tu dispositivo. Usa los canales nativos V2.");
        Console.WriteLine("  --advanced-bridge Test de 3 fases: Unlock→ECDH Handshake→Captura 10s.");
        Console.WriteLine("                  Diagnostica si FF 04 00 desbloquea el handshake ECDH.");
    }

    private static IAuthChallengeClient? BuildAuthClientFromArgs(string[] args)
    {
        if (args.Any(a => a.Equals("--auth-replay-hardcoded", StringComparison.OrdinalIgnoreCase)))
        {
            if (!AuthReplayPayloads.TryGetDefaultPayload(out byte[] hardcoded))
            {
                Console.WriteLine("⚠️  --auth-replay-hardcoded activo, pero no hay payload configurado en AuthReplayPayloads.");
                return null;
            }

            return new ReplayAuthChallengeClient(hardcoded, "--auth-replay-hardcoded");
        }

        string? replayHex = GetOptionValue(args, "--auth-replay-hex");
        if (!string.IsNullOrWhiteSpace(replayHex))
        {
            byte[] bytes = ParseHexPayload(replayHex);
            return new ReplayAuthChallengeClient(bytes, "--auth-replay-hex");
        }

        string? replayFile = GetOptionValue(args, "--auth-replay-file");
        if (!string.IsNullOrWhiteSpace(replayFile) && File.Exists(replayFile))
        {
            string hex = File.ReadAllText(replayFile).Trim();
            byte[] bytes = ParseHexPayload(hex);
            return new ReplayAuthChallengeClient(bytes, $"--auth-replay-file:{Path.GetFileName(replayFile)}");
        }

        return null;
    }

    private static string? GetOptionValue(string[] args, string option)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static string GetDeviceNameArg(string[] args, string defaultName = "Zwift Click")
    {
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("-", StringComparison.Ordinal))
                return token;

            if (OptionRequiresValue(token) && i + 1 < args.Length)
                i++;
        }

        return defaultName;
    }

    private static bool OptionRequiresValue(string option)
    {
        return option.Equals("--auth-replay-hex", StringComparison.OrdinalIgnoreCase)
            || option.Equals("--auth-replay-file", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ParseHexPayload(string hex)
    {
        string compact = new string(hex.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (compact.Length == 0)
            return Array.Empty<byte>();
        if (compact.Length % 2 != 0)
            throw new ArgumentException("El payload hex debe tener longitud par.");

        byte[] result = new byte[compact.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16);
        return result;
    }
}
