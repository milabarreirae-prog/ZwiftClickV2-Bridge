using System.Security.Cryptography;
using ZwiftClickV2.Bridge.Auth;
using ZwiftClickV2.Bridge.Bridge;
using ZwiftClickV2.Bridge.Crypto;

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

        bool legacyHkdf = HasFlag(args, "--legacy-hkdf-info");
        bool noKeyboard = HasFlag(args, "--no-keyboard");
        HkdfInfoMode hkdfMode = legacyHkdf ? HkdfInfoMode.LegacyHandshakeData : HkdfInfoMode.Empty;

        switch (args[0].ToLowerInvariant())
        {
            case "--test-crypto":
                return RunCryptoSelfTest(hkdfMode);

            case "--diagnose":
            case "-d":
                return await RunBridgeAsync(GetDeviceNameArg(args), accessToken: null, emulateKeyboard: false);

            case "--andriuz":
            case "--v1":
                return await RunAndriuzAsync(GetDeviceNameArg(args), emulateKeyboard: HasFlag(args, "--keyboard"));

            case "--unlock":
            case "--bridge":
            case "-b":
            {
                string? token = await ResolveAccessTokenAsync();
                if (token == null)
                {
                    Console.WriteLine("❌ No se pudo obtener un token de tu cuenta Zwift.");
                    Console.WriteLine("   Define ZWIFT_ACCESS_TOKEN, o ZWIFT_USERNAME+ZWIFT_PASSWORD, o ZWIFT_REFRESH_TOKEN,");
                    Console.WriteLine("   o ejecuta en una terminal interactiva para introducir tus credenciales.");
                    return 1;
                }
                return await RunBridgeAsync(GetDeviceNameArg(args), token, emulateKeyboard: !noKeyboard);
            }

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

    /// <summary>
    /// Resuelve un access_token de la cuenta DEL USUARIO: primero del entorno
    /// (ZWIFT_ACCESS_TOKEN / ZWIFT_USERNAME+ZWIFT_PASSWORD / ZWIFT_REFRESH_TOKEN) y, si no hay nada
    /// y la terminal es interactiva, pide usuario y contraseña. Nunca embebe ni guarda el token.
    /// </summary>
    private static async Task<string?> ResolveAccessTokenAsync()
    {
        using var oauth = new ZwiftOAuthClient();
        try
        {
            string? token = await oauth.ResolveTokenFromEnvAsync();
            if (token != null) return token;

            var credentials = ZwiftCredentials.Resolve();
            if (credentials != null) return await oauth.LoginAsync(credentials);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Login Zwift falló: {ex.Message}");
        }
        return null;
    }

    private static async Task<int> RunBridgeAsync(string deviceName, string? accessToken, bool emulateKeyboard)
    {
        using var bridge = new ZwiftClickBridge(emulateKeyboard);
        bool success = await bridge.StartAsync(deviceName, accessToken);

        if (success)
        {
            Console.WriteLine("\nPresiona ENTER para detener el bridge...");
            Console.ReadLine();
            bridge.Stop();
            return 0;
        }
        return 1;
    }

    /// <summary>
    /// Modo V1/andriuz headless (diagnóstico): conecta EN CLARO, habilita y escucha los botones
    /// (bitmask 2308…0F) ~2 min, imprimiendo cada botón decodificado y los bits sin mapear. SIN cuenta.
    /// Por defecto NO emula teclas (para no escribir en ventanas ajenas); usa --keyboard para activarlo.
    /// </summary>
    private static async Task<int> RunAndriuzAsync(string deviceName, bool emulateKeyboard)
    {
        using var bridge = new ZwiftClickBridge(emulateKeyboard);

        // Mapear los 10 botones para que cada uno se imprima al pulsarlo (teclas MyWhoosh).
        bridge.SetActions(
            new Dictionary<string, byte>
            {
                ["plus"] = KeyboardEmulator.VK_I,    ["minus"] = KeyboardEmulator.VK_K,
                ["left"] = KeyboardEmulator.VK_LEFT,  ["right"] = KeyboardEmulator.VK_RIGHT,
                ["nav_up"] = KeyboardEmulator.VK_U,   ["nav_down"] = KeyboardEmulator.VK_H,
                ["btn_a"] = KeyboardEmulator.VK_1,    ["btn_b"] = KeyboardEmulator.VK_2,
                ["btn_x"] = KeyboardEmulator.VK_3,    ["btn_y"] = KeyboardEmulator.VK_4,
            },
            new Dictionary<string, string>
            {
                ["plus"] = "+ (subir)", ["minus"] = "− (bajar)", ["left"] = "← izquierda", ["right"] = "→ derecha",
                ["nav_up"] = "↑ arriba", ["nav_down"] = "↓ abajo", ["btn_a"] = "A", ["btn_b"] = "B",
                ["btn_x"] = "X", ["btn_y"] = "Y",
            });

        bridge.ProgressChanged += p => Console.WriteLine($"   · {p.Message}");
        bridge.DiagnosticFrame += f => Console.WriteLine($"   🔬 {f}");
        bridge.ButtonEmitted += b => Console.WriteLine($"🎮 BOTÓN: {b.Label}  → tecla 0x{b.VirtualKey:X2}  [{b.ActionId}]");

        bool ok = await bridge.StartAndriuzAsync(deviceName);
        if (!ok) { Console.WriteLine("❌ No se pudo iniciar el modo V1 (¿mando apagado o ya conectado en otra app?)."); return 1; }

        Console.WriteLine("\n⏱️  Escuchando 120 s. Pulsa TODOS los botones del mando, uno a uno, con pausas…");
        await Task.Delay(TimeSpan.FromSeconds(120));
        bridge.Stop();
        Console.WriteLine("✅ Fin de la escucha. (Log crudo en logs/.)");
        return 0;
    }

    /// <summary>
    /// Self-test de criptografía sin hardware: deriva una sesión V2 entre dos pares EC y verifica
    /// un round-trip AES-256-CCM con los parámetros confirmados.
    /// </summary>
    private static int RunCryptoSelfTest(HkdfInfoMode hkdfMode)
    {
        Console.WriteLine($"== Self-test cripto (HKDF info = {hkdfMode}) ==");
        try
        {
            using var bridgeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var deviceKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            byte[] bridgePub = ExportPub(bridgeKey);
            byte[] devicePub = ExportPub(deviceKey);

            var a = new ZPEncryptionV2(hkdfMode);
            a.Initialize(bridgeKey, devicePub, devicePub, bridgePub);
            var b = new ZPEncryptionV2(hkdfMode);
            b.Initialize(deviceKey, bridgePub, devicePub, bridgePub);

            byte[] msg = "ZwiftClickV2"u8.ToArray();
            byte[] ct = a.Encrypt(msg, counter: 7, associatedData: null);
            byte[] pt = b.Decrypt(ct, counter: 7, associatedData: null);

            bool ok = pt.AsSpan().SequenceEqual(msg) && a.AesKey.Length == 32 && a.HkdfSalt.Length == 128;
            Console.WriteLine(ok ? "✅ PASS — round-trip AES-256-CCM, key 32B, salt 128B." : "❌ FAIL");
            Console.WriteLine($"   pubkey comprimida (33B): {Convert.ToHexString(EcPoint.Compress(devicePub.AsSpan(1, 64).ToArray()))[..10]}…");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ FAIL — {ex.Message}");
            return 1;
        }
    }

    private static byte[] ExportPub(ECDiffieHellman ecdh)
    {
        var p = ecdh.ExportParameters(false);
        byte[] k = new byte[65]; k[0] = 0x04;
        Array.Copy(p.Q.X!, 0, k, 1, 32);
        Array.Copy(p.Q.Y!, 0, k, 33, 32);
        return k;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ZwiftClickV2-Bridge");
        Console.WriteLine();
        Console.WriteLine("Uso:");
        Console.WriteLine("  ZwiftClickV2-Bridge --bridge   [deviceName]   Unlock completo + emulación de teclado");
        Console.WriteLine("  ZwiftClickV2-Bridge --unlock   [deviceName]   Igual que --bridge (alias)");
        Console.WriteLine("  ZwiftClickV2-Bridge --andriuz  [deviceName]   Modo V1 SIN cuenta: botones en claro (bitmask 2308…0F)");
        Console.WriteLine("  ZwiftClickV2-Bridge --diagnose [deviceName]   Solo handshake BLE (sin red, sin teclado)");
        Console.WriteLine("  ZwiftClickV2-Bridge --test-crypto             Self-test de cripto (no requiere hardware)");
        Console.WriteLine("  ZwiftClickV2-Bridge --help                    Esta ayuda");
        Console.WriteLine();
        Console.WriteLine("Flags:");
        Console.WriteLine("  --no-keyboard         No emular teclas (solo registrar eventos)");
        Console.WriteLine("  --legacy-hkdf-info    Self-test con HKDF info = \"handshake data\" (V1) en vez de vacío.");
        Console.WriteLine("                        (El bridge real auto-resuelve la cripto de sesión por bake-off.)");
        Console.WriteLine();
        Console.WriteLine("Cuenta Zwift (diseño ético — login con TU cuenta, nunca un token embebido):");
        Console.WriteLine("  El unlock es server-backed y requiere el token de tu propia cuenta Zwift.");
        Console.WriteLine("  Provee las credenciales por entorno: ZWIFT_USERNAME / ZWIFT_PASSWORD,");
        Console.WriteLine("  o se pedirán de forma interactiva. Solo se usan en memoria.");
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string GetDeviceNameArg(string[] args, string defaultName = "Zwift Click")
    {
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("-", StringComparison.Ordinal))
                return token;
        }
        return defaultName;
    }
}
