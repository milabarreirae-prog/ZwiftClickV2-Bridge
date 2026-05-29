using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using ZwiftClickV2.Bridge.BLE;
using ZwiftClickV2.Bridge.Crypto;
using ZwiftClickV2.Bridge.Logging;
using ZwiftClickV2.Bridge.Protocol;
using ZwiftClickV2.Bridge.Protocol.Messages;

namespace ZwiftClickV2.Bridge.Bridge;

/// <summary>
/// Implementa el "Hot Pairing Bridge" — la secuencia de handshake ECDH
/// optimizada que BikeControl usa para conectarse al Zwift Click V2.
/// 
/// Principios clave de BikeControl (extraídos de ingeniería inversa):
/// 1. Conectar sin lecturas previas ni suscripciones innecesarias
/// 2. Suscribir CH04 (Indicate) PRIMERO para no perder la respuesta
/// 3. Enviar handshake ECDH como PRIMER write a CH03
/// 4. NO enviar comandos de inicialización, lecturas, ni pings antes del handshake
/// 5. Si el dispositivo responde con clave pública → éxito, derivar AES key
/// 6. Si el dispositivo responde con datos de estado/rechazo → está bloqueado (DRM)
/// 
/// Workflow "Hot Pairing":
///   1. Abrir Zwift oficial → conectar Click V2 (30s) → LED azul/verde
///   2. Cerrar Zwift completamente
///   3. Ejecutar este bridge INMEDIATAMENTE
///   4. Hipótesis actual: el sistema/dispositivo conserva un estado temporal de confianza o bonding
/// </summary>
public class HotPairBridge : IDisposable
{
    private readonly BleDeviceManager _ble = new();
    private readonly BleCharacteristicWriter _writer = new();
    private readonly BleNotificationListener _listener = new();
    private readonly KeyboardEmulator _keyboard = new();
    private readonly StructuredLogger _logger = new();
    private readonly IAuthChallengeClient _authClient;
    private readonly bool _authDryRun;

    private ZopSequencer? _sequencer;
    private IZPEncryption? _crypto;
    private ApplicationLayerParser? _appParser;
    private AuthFlowCoordinator? _authCoordinator;
    private ZPEncryptionV2? _cryptoV2;
    private CancellationTokenSource? _keepAliveCts;
    private ECDiffieHellman? _ourEcdhKey;
    private byte[]? _ourPublicKey65;
    private bool _stopped;

    public HotPairBridge(IAuthChallengeClient? authClient = null, bool authDryRun = false)
    {
        _authClient = authClient ?? new NullAuthChallengeClient();
        _authDryRun = authDryRun;
    }

    public bool IsOperational => _ble.IsConnected && _crypto?.IsInitialized == true;

    /// <summary>
    /// Estados del dispositivo detectados durante el diagnóstico.
    /// </summary>
    public enum DeviceState
    {
        Unknown,
        /// <summary>Responde con eco "RideOn" (6B) — dispositivo dormido, requiere ciclo de encendido.</summary>
        Sleeping_EchoOnly,
        /// <summary>Responde con error estructurado (RideOn 02 03 + datos) — bloqueado por DRM.</summary>
        Locked_ErrorResponse,
        /// <summary>Responde con clave pública EC — listo para handshake.</summary>
        Unlocked_PublicKey,
        /// <summary>No responde — posiblemente apagado o desconectado.</summary>
        NoResponse
    }

    /// <summary>
    /// Ejecuta el Hot Pairing completo.
    /// </summary>
    public async Task<bool> StartAsync(string deviceName = "Zwift Click", bool diagnoseCrypto = false)
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  ZwiftClickV2 Hot-Pair Bridge");
        Console.WriteLine("  Secuencia: BikeControl-style ECDH handshake");
        Console.WriteLine("═══════════════════════════════════════════\n");

        var sw = Stopwatch.StartNew();

        // ── Paso 1: Conectar BLE (sin lecturas previas) ─────────────────
        var device = await _ble.ConnectAsync(deviceName);
        if (device == null) return false;
        Console.WriteLine($"   ⏱  Conexión BLE: {sw.ElapsedMilliseconds}ms");
        _logger.LogInfo($"Hot-pair connected in {sw.ElapsedMilliseconds}ms");

        // ── Paso 2: Obtener características (solo CH03 y CH04) ──────────
        var service = await _ble.GetServiceAsync(BleDeviceManager.ZWIFT_SERVICE_UUID);
        if (service == null)
        {
            Console.WriteLine("❌ Servicio Zwift no encontrado");
            return false;
        }

        var charsResult = await service.GetCharacteristicsAsync();
        var allChars = charsResult.Characteristics;

        var ch03 = allChars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH03_UUID);
        var ch04 = allChars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH04_UUID);

        if (ch03 == null) { Console.WriteLine("❌ CH03 (SyncTx) no encontrado"); return false; }
        if (ch04 == null) { Console.WriteLine("❌ CH04 (SyncRx) no encontrado"); return false; }

        _writer.RegisterCharacteristic(BleDeviceManager.CH03_UUID, ch03);
        Console.WriteLine($"   ⏱  Características obtenidas: {sw.ElapsedMilliseconds}ms");

        // ── Paso 3: Handshake ECDH (PRIMER write al dispositivo) ────────
        Console.WriteLine("\n[HANDSHAKE] ═══════════════════════════════");
        var handshakeResult = await PerformEcdhHandshakeAsync(ch03, ch04, sw);
        if (!handshakeResult.success)
        {
            Console.WriteLine("\n❌ HANDSHAKE FALLIDO");
            Console.WriteLine("   El dispositivo no aceptó la sesión segura.");
            Console.WriteLine("   Hipótesis actual: falta estado temporal de confianza/bonding.");
            Console.WriteLine("   Acción: abre Zwift oficial, conecta el Click V2 30s,");
            Console.WriteLine("   cierra Zwift y vuelve a ejecutar este bridge inmediatamente.");
            _logger.LogInfo("Hot-pair failed during handshake");
            return false;
        }

        // ── Paso 4: Session setup — inicializar canal seguro ────────────
        Console.WriteLine("\n[SESSION-SETUP] ═══════════════════════════");

        // Registrar CH02 para escritura (botones)
        var ch02 = allChars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH02_UUID);
        var ch100 = allChars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH100_UUID);
        var ch101 = allChars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH101_UUID);
        var ch102 = allChars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH102_UUID);
        if (ch02 != null)
            _writer.RegisterCharacteristic(BleDeviceManager.CH02_UUID, ch02);

        await SendPostHandshakeMessagesAsync(ch03);

        if (diagnoseCrypto)
            PrintCryptoDiagnostics();

        // ── Paso 5: Suscribirse a canales de aplicación ─────────────────
        await SubscribeToApplicationChannelsAsync(ch02, ch04, ch100, ch101, ch102);

        // ── Paso 6: Iniciar keep-alive ──────────────────────────────────
        StartKeepAlive(ch03);

        Console.WriteLine($"\n✅ Bridge operativo ({sw.ElapsedMilliseconds}ms total)");
        Console.WriteLine("   Escuchando eventos de botones...\n");
        _logger.LogInfo($"Hot-pair operational in {sw.ElapsedMilliseconds}ms");
        return true;
    }

    /// <summary>
    /// Realiza el handshake ECDH siguiendo la secuencia exacta de BikeControl:
    /// 1. Suscribir CH04 (Indicate) PRIMERO
    /// 2. Enviar "RideOn" + 0x01 0x02 + pubkey[64] como primer write a CH03
    /// 3. Esperar respuesta en CH04 (< 2 segundos)
    /// 4. Parsear respuesta: debe ser "RideOn" + 0x01 0x03 + pubkey[64] en wire
    /// </summary>
    private async Task<(bool success, byte[]? response)> PerformEcdhHandshakeAsync(
        GattCharacteristic ch03, GattCharacteristic ch04, Stopwatch sw)
    {
        // ── 1. Generar par de claves efímeras P-256 ─────────────────────
        Console.WriteLine("[HANDSHAKE] Generando claves P-256...");
        _ourEcdhKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ourParams = _ourEcdhKey.ExportParameters(false);

        byte[] ourPubKey65 = new byte[65];
        ourPubKey65[0] = 0x04;
        Array.Copy(ourParams.Q.X!, 0, ourPubKey65, 1, 32);
        Array.Copy(ourParams.Q.Y!, 0, ourPubKey65, 33, 32);
        _ourPublicKey65 = ourPubKey65;

        Console.WriteLine($"[HANDSHAKE] Nuestra clave pública X: {BitConverter.ToString(ourParams.Q.X!).Replace("-", "")[..32]}...");
        Console.WriteLine($"[HANDSHAKE] Nuestra clave pública Y: {BitConverter.ToString(ourParams.Q.Y!).Replace("-", "")[..32]}...");

        // ── 2. Construir payload: "RideOn" + 0x01 + 0x02 + PublicKey(64B wire) ──
        var hello = new ZopHello { PublicKey = ourPubKey65, Suffix = new byte[] { 0x01, 0x02 } };
        byte[] payload = hello.BuildHandshakePayload();

        Console.WriteLine($"[HANDSHAKE] Payload ({payload.Length}B): {BitConverter.ToString(payload).Replace("-", "")[..48]}...");
        Console.WriteLine($"[HANDSHAKE] Formato: 'RideOn'(6) + {BitConverter.ToString(hello.Suffix).Replace("-", " ")} + pubkey[64]");

        // ── 3. Suscribir CH04 PRIMERO (Indicate) ────────────────────────
        var responseTcs = new TaskCompletionSource<byte[]>();
        var handshakeSw = Stopwatch.StartNew();

        await _listener.SubscribeAsync(BleDeviceManager.CH04_UUID, ch04, data =>
        {
            handshakeSw.Stop();
            Console.WriteLine($"[HANDSHAKE] Respuesta recibida en {handshakeSw.ElapsedMilliseconds}ms");
            Console.WriteLine($"[HANDSHAKE] Raw ({data.Length}B): {BitConverter.ToString(data).Replace("-", "")}");
            responseTcs.TrySetResult(data);
        }, useIndicate: true);

        // ── 4. ENVIAR handshake como PRIMER write ───────────────────────
        Console.WriteLine("[HANDSHAKE] Enviando handshake a CH03...");
        var writer = new DataWriter();
        writer.WriteBytes(payload);
        var writeStatus = await ch03.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
        Console.WriteLine($"[HANDSHAKE] Write status: {writeStatus}");

        // ── 5. Esperar respuesta (timeout 3 segundos) ───────────────────
        byte[]? response;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            response = await responseTcs.Task.WaitAsync(cts.Token);
        }
        catch (TimeoutException)
        {
            Console.WriteLine("[HANDSHAKE] ❌ Timeout esperando respuesta en CH04");
            await _listener.UnsubscribeAsync(BleDeviceManager.CH04_UUID);
            return (false, null);
        }

        // ── 6. Analizar respuesta ───────────────────────────────────────
        var analysis = AnalyzeHandshakeResponse(response);
        Console.WriteLine($"[HANDSHAKE] Análisis: {analysis}");
        _logger.Log("handshake_response", "rx", "CH04", response, $"analysis={analysis}");

        if (analysis == HandshakeResult.Success_WithPublicKey)
        {
            // Extraer clave pública del dispositivo
            var parsed = ZopWelcome.Parse(response);
            if (parsed.PublicKey.Length == 65)
            {
                Console.WriteLine($"[HANDSHAKE] ✅ Peer pubkey detectada (65B)");
                Console.WriteLine($"[HANDSHAKE] Sufijo: {BitConverter.ToString(parsed.Suffix).Replace("-", " ")}");
                Console.WriteLine($"[HANDSHAKE] Peer X: {BitConverter.ToString(parsed.PublicKey.AsSpan(1, 32).ToArray()).Replace("-", "")[..32]}...");

                // ── 7. Derivar clave de sesión ──────────────────────────
                InitializeCrypto(parsed.PublicKey, parsed.Suffix);
                await _listener.UnsubscribeAsync(BleDeviceManager.CH04_UUID);
                return (true, response);
            }
        }

        // ── 8. Diagnóstico detallado del rechazo ────────────────────────
        DiagnoseRejection(response);
        await _listener.UnsubscribeAsync(BleDeviceManager.CH04_UUID);
        return (false, response);
    }

    /// <summary>
    /// Inicializa el cifrado después de un handshake exitoso.
    /// Detecta V1 vs V2 y configura la clave AES correspondiente.
    /// </summary>
    private void InitializeCrypto(byte[] peerPublicKey65, byte[] suffix)
    {
        Console.WriteLine("[HANDSHAKE] Derivando shared secret ECDH...");

        // Importar clave pública del peer
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

        byte[] sharedSecret = _ourEcdhKey!.DeriveKeyMaterial(peerEcdh.PublicKey);
        Console.WriteLine($"[HANDSHAKE] Shared secret: {BitConverter.ToString(sharedSecret).Replace("-", "")[..32]}...");

        // Detectar versión y crear cifrado
        var version = ZPEncryptionFactory.DetectVersion(suffix);
        Console.WriteLine($"[HANDSHAKE] Protocolo detectado: {version}");

        if (version == ZPEncryptionFactory.ProtocolVersion.V2)
        {
            _cryptoV2 = new ZPEncryptionV2();
            if (_ourPublicKey65 == null)
                throw new InvalidOperationException("La clave pública local no está disponible para inicializar V2.");

            _cryptoV2.Initialize(_ourEcdhKey, peerPublicKey65, peerPublicKey65, _ourPublicKey65);
            _crypto = new ZPEncryptionAdapterV2(_cryptoV2);
        }
        else
        {
            _cryptoV2 = null;
            _crypto = ZPEncryptionFactory.Create(version);
            _crypto.InitializeV1(_ourEcdhKey, peerPublicKey65);
        }

        _sequencer = new ZopSequencer(_crypto);
        _appParser = new ApplicationLayerParser(_crypto, _sequencer);
        _authCoordinator = new AuthFlowCoordinator(_authClient, _logger);
        Console.WriteLine("[HANDSHAKE] ✅ Clave de sesión establecida");
    }

    /// <summary>
    /// Envía los mensajes post-handshake: Capability, Ping, Empty, Ping.
    /// </summary>
    private async Task SendPostHandshakeMessagesAsync(GattCharacteristic syncTx)
    {
        Console.WriteLine("[POST] Enviando writes ZOP...");

        byte[] capFrame = _sequencer!.PackageAndEncrypt(ZopCapability.CreateDefault().Payload);
        await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, capFrame);
        Console.WriteLine($"   ✅ Write 1 (Capability) seq=0: {capFrame.Length}B");

        byte[] pingFrame = _sequencer.PackageAndEncrypt(ZopPing.Payload);
        await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, pingFrame);
        Console.WriteLine($"   ✅ Write 2 (Ping) seq=1: {pingFrame.Length}B");

        byte[] e3 = _sequencer.PackageAndEncrypt(Array.Empty<byte>());
        await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, e3);
        Console.WriteLine($"   ✅ Write 3 (Empty) seq=2: {e3.Length}B");

        byte[] e4 = _sequencer.PackageAndEncrypt(ZopPing.Payload);
        await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, e4);
        Console.WriteLine($"   ✅ Write 4 (Ping) seq=3: {e4.Length}B");
    }

    /// <summary>
    /// Suscribe canales de aplicación y enruta por handle ATT origen.
    /// </summary>
    private async Task SubscribeToApplicationChannelsAsync(
        GattCharacteristic? ch02,
        GattCharacteristic? ch04,
        GattCharacteristic? ch100,
        GattCharacteristic? ch101,
        GattCharacteristic? ch102)
    {
        if (_appParser == null)
            return;

        if (ch02 != null)
        {
            await _listener.SubscribeAsync(BleDeviceManager.CH02_UUID, ch02, ctx =>
            {
                HandleApplicationNotification(ctx.AttributeHandle, "CH02", ctx.Data);
            });
        }

        if (ch04 != null)
        {
            await _listener.SubscribeAsync(BleDeviceManager.CH04_UUID, ch04, ctx =>
            {
                HandleApplicationNotification(ctx.AttributeHandle, "CH04", ctx.Data);
            }, useIndicate: true);
        }

        if (ch100 != null)
        {
            await _listener.SubscribeAsync(BleDeviceManager.CH100_UUID, ch100, ctx =>
            {
                HandleApplicationNotification(ctx.AttributeHandle, "CH100", ctx.Data);
            }, useIndicate: true);
        }

        if (ch101 != null)
        {
            await _listener.SubscribeAsync(BleDeviceManager.CH101_UUID, ch101, ctx =>
            {
                HandleApplicationNotification(ctx.AttributeHandle, "CH101", ctx.Data);
            }, useIndicate: true);
        }

        if (ch102 != null)
        {
            await _listener.SubscribeAsync(BleDeviceManager.CH102_UUID, ch102, ctx =>
            {
                HandleApplicationNotification(ctx.AttributeHandle, "CH102", ctx.Data);
            });
        }
    }

    private void HandleApplicationNotification(ushort handle, string channelName, byte[] data)
    {
        Console.WriteLine($"\n🎮 [{channelName}] handle=0x{handle:X4} ({data.Length}B): {BitConverter.ToString(data).Replace("-", "")}");
        _logger.Log("app_rx_raw", "rx", channelName, data, $"handle=0x{handle:X4}");

        var parsed = _appParser!.Parse(handle, data);
        Console.WriteLine($"   ℹ️  channel={parsed.Channel} encrypted={parsed.WasEncrypted} opcode=0x{parsed.ApplicationOpcode:X2} {parsed.SymbolicName}");

        _logger.Log(
            "app_rx_parsed",
            "rx",
            parsed.Channel.ToString(),
            parsed.Plaintext,
            $"opcode=0x{parsed.ApplicationOpcode:X2}; symbolic={parsed.SymbolicName}; encrypted={parsed.WasEncrypted}; counter={(parsed.Counter?.ToString() ?? "N/A")}");

        _ = TryProcessAuthFlowAsync(parsed);

        if (parsed.Channel != ZapChannel.CH02_AsyncNotify || !parsed.WasEncrypted || parsed.Plaintext.Length == 0)
            return;

        byte[] eventPayload = parsed.ApplicationOpcode is 0x37 or 0x38
            ? parsed.Plaintext.AsSpan(1).ToArray()
            : parsed.Plaintext;

        if (eventPayload.Length == 0)
            return;

        var evt = ZopPeripheralEvent.Parse(eventPayload);
        Console.WriteLine($"   ✅ Evento: {evt}");

        byte vk = evt.ToVirtualKey();
        if (vk != 0)
        {
            _keyboard.SendKeyPress(vk);
            Console.WriteLine($"   ⌨️  Tecla emulada: 0x{vk:X2}");
            _logger.LogInfo($"Keyboard emulation sent vk=0x{vk:X2}");
        }
    }

    private async Task TryProcessAuthFlowAsync(ParsedZapMessage parsed)
    {
        if (_authCoordinator == null)
            return;

        try
        {
            var decision = await _authCoordinator.TryResolveAsync(parsed);
            if (!decision.ShouldSendResponse || decision.ResponsePayload.Length == 0)
                return;

            if (!_writer.IsRegistered(BleDeviceManager.CH03_UUID))
            {
                _logger.LogError("Auth response ready but CH03 is not registered for write");
                return;
            }

            if (_authDryRun)
            {
                _logger.Log("auth_response_dry_run", "tx", "CH03", decision.ResponsePayload, decision.Notes);
                return;
            }

            await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, decision.ResponsePayload);
            _logger.Log("auth_response_sent", "tx", "CH03", decision.ResponsePayload, decision.Notes);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Auth flow processing error: {ex.Message}");
        }
    }

    private void StartKeepAlive(GattCharacteristic syncTx)
    {
        _keepAliveCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_keepAliveCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, _keepAliveCts.Token);
                    if (_sequencer != null && _writer.IsRegistered(BleDeviceManager.CH03_UUID))
                    {
                        byte[] ping = _sequencer.PackageAndEncrypt(ZopPing.Payload);
                        await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, ping);
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex) { Console.WriteLine($"   ⚠️  Keep-alive error: {ex.Message}"); }
            }
        });
    }

    // ── Análisis de respuesta de handshake ──────────────────────────────

    private enum HandshakeResult
    {
        Unknown,
        Success_WithPublicKey,
        Rejection_StatusMessage,
        Rejection_Error58_02,
        Rejection_ErrorC0_03,
        Rejection_BatteryData,
        Rejection_EmptyOrShort
    }

    private static HandshakeResult AnalyzeHandshakeResponse(byte[] response)
    {
        if (response == null || response.Length < 8)
            return HandshakeResult.Rejection_EmptyOrShort;

        // ¿Empieza con "RideOn"?
        bool isRideOn = response.Length >= 6 &&
            response[0] == 'R' && response[1] == 'i' && response[2] == 'd' &&
            response[3] == 'e' && response[4] == 'O' && response[5] == 'n';

        if (!isRideOn)
        {
            // ¿Es clave pública directamente? (0x04 + 64 bytes)
            if (response.Length >= 65 && response[0] == 0x04)
                return HandshakeResult.Success_WithPublicKey;
            return HandshakeResult.Unknown;
        }

        // Extraer sufijo
        byte[] suffix = response.Length >= 8
            ? new byte[] { response[6], response[7] }
            : Array.Empty<byte>();

        // ¿01 03? → Respuesta V2 con clave pública en wire raw[64]
        if (suffix.Length == 2 && suffix[0] == 0x01 && suffix[1] == 0x03)
        {
            if (HandshakeParser.ExtractRawPublicKey(response) != null)
                return HandshakeResult.Success_WithPublicKey;
            return HandshakeResult.Unknown;
        }

        // ¿02 03? → Rechazo o estado Protobuf
        if (suffix.Length == 2 && suffix[0] == 0x02 && suffix[1] == 0x03)
        {
            // Analizar datos Protobuf después del header
            byte[] afterHeader = response.Skip(8).ToArray();

            // 58 02 → Field 11 = 2 (error de autenticación)
            if (afterHeader.Length >= 2 && afterHeader[0] == 0x58 && afterHeader[1] == 0x02)
                return HandshakeResult.Rejection_Error58_02;

            // C0 03 → Field 24 = 3 (error de formato)
            if (afterHeader.Length >= 2 && afterHeader[0] == 0xC0 && afterHeader[1] == 0x03)
                return HandshakeResult.Rejection_ErrorC0_03;

            // 10 64 → Field 2 = 100 (batería)
            if (afterHeader.Length >= 2 && afterHeader[0] == 0x10)
                return HandshakeResult.Rejection_BatteryData;

            return HandshakeResult.Rejection_StatusMessage;
        }

        // ¿01 02 o 00 09? → Handshake V1 exitoso
        if (suffix.Length == 2 &&
            ((suffix[0] == 0x01 && suffix[1] == 0x02) ||
             (suffix[0] == 0x00 && suffix[1] == 0x09)))
        {
            if (HandshakeParser.ExtractPublicKey(response) != null)
                return HandshakeResult.Success_WithPublicKey;
        }

        return HandshakeResult.Unknown;
    }

    private static void DiagnoseRejection(byte[] response)
    {
        Console.WriteLine("\n[HANDSHAKE] ═════════ DIAGNÓSTICO DE RECHAZO ═════════");

        if (response == null || response.Length < 8)
        {
            Console.WriteLine("   ❌ Respuesta vacía o muy corta");
            Console.WriteLine("   → El dispositivo no respondió al handshake");
            return;
        }

        string ascii = Encoding.ASCII.GetString(response.Where(b => b >= 32 && b < 127).ToArray());
        Console.WriteLine($"   ASCII visible: \"{ascii}\"");

        // Decodificar Protobuf básico
        if (response.Length >= 8 && response[6] == 0x02 && response[7] == 0x03)
        {
            byte[] protoBytes = response.Skip(8).ToArray();
            Console.WriteLine($"   Protobuf data ({protoBytes.Length}B): {BitConverter.ToString(protoBytes).Replace("-", " ")}");

            // Interpretar campos Protobuf conocidos
            int pos = 0;
            while (pos < protoBytes.Length)
            {
                if (pos >= protoBytes.Length) break;
                byte tag = protoBytes[pos];
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x07;

                if (wireType == 0) // Varint
                {
                    ulong value = 0;
                    int shift = 0;
                    pos++;
                    while (pos < protoBytes.Length)
                    {
                        byte b = protoBytes[pos++];
                        value |= (ulong)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0) break;
                        shift += 7;
                    }

                    string interpretation = fieldNumber switch
                    {
                        2 => "Batería %",
                        3 => "Dial 1",
                        4 => "Dial 2",
                        11 => "⚠️  Error auth (58 02 = autenticación rechazada)",
                        24 => "⚠️  Error formato (C0 03 = formato inválido)",
                        _ => ""
                    };
                    Console.WriteLine($"   Field {fieldNumber} (varint): {value} {interpretation}");
                }
                else if (wireType == 2) // Length-delimited
                {
                    pos++;
                    if (pos >= protoBytes.Length) break;
                    int length = protoBytes[pos++];
                    byte[] value = protoBytes.Skip(pos).Take(Math.Min(length, protoBytes.Length - pos)).ToArray();
                    pos += length;
                    Console.WriteLine($"   Field {fieldNumber} (bytes {length}B): {BitConverter.ToString(value).Replace("-", " ")}");
                }
                else
                {
                    pos++; // Skip unknown wire types
                }
            }
        }

        Console.WriteLine("\n   ═══════════════════════════════════════════");
        Console.WriteLine("   🔒 DISPOSITIVO NO AUTORIZADO PARA HANDSHAKE");
        Console.WriteLine("   ═══════════════════════════════════════════");
        Console.WriteLine("   Siguiente hipótesis a verificar:");
        Console.WriteLine("   1. Abre Zwift oficial en tu PC");
        Console.WriteLine("   2. Ve a la pantalla de dispositivos y conecta el Click V2");
        Console.WriteLine("   3. Espera ~30 segundos (LED debe ponerse azul/verde)");
        Console.WriteLine("   4. Cierra Zwift COMPLETAMENTE (verifica en Admin. de Tareas)");
        Console.WriteLine("   5. Ejecuta este bridge INMEDIATAMENTE para reutilizar el estado de confianza");
        Console.WriteLine("   ═══════════════════════════════════════════");
    }

    /// <summary>
    /// Diagnostica el estado del dispositivo enviando solo "RideOn" (sin pubkey).
    /// Esto permite clasificar en qué nivel de bloqueo se encuentra:
    /// 
    /// - Sleeping (eco 6B):  El dispositivo solo hace eco, necesita ciclo de encendido
    /// - Locked (error 72B):  El dispositivo rechaza activamente, requiere Zwift oficial
    /// - Unlocked (pubkey):   El dispositivo responde con clave pública, listo para handshake
    /// - NoResponse:          El dispositivo no responde
    /// </summary>
    /// <param name="deviceName">Nombre del dispositivo a escanear/conectar.</param>
    /// <returns>El estado detectado y la respuesta cruda (si la hubo).</returns>
    public static async Task<(DeviceState State, byte[]? RawResponse)> DiagnoseDeviceState(string deviceName = "Zwift Click")
    {
        Console.WriteLine("\n[DIAGNOSTICO] ════════════════════════════");
        Console.WriteLine("[DIAGNOSTICO] Enviando 'RideOn' solo (6B) para clasificar estado...");

        var ble = new BleDeviceManager();
        var listener = new BleNotificationListener();

        try
        {
            // 1. Conectar
            var device = await ble.ConnectAsync(deviceName);
            if (device == null)
            {
                Console.WriteLine("[DIAGNOSTICO] ❌ No se pudo conectar al dispositivo");
                return (DeviceState.NoResponse, null);
            }

            // 2. Obtener características
            var service = await ble.GetServiceAsync(BleDeviceManager.ZWIFT_SERVICE_UUID);
            if (service == null)
            {
                Console.WriteLine("[DIAGNOSTICO] ❌ Servicio Zwift no encontrado");
                return (DeviceState.NoResponse, null);
            }

            var charsResult = await service.GetCharacteristicsAsync();
            var ch03 = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH03_UUID);
            var ch04 = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH04_UUID);

            if (ch03 == null || ch04 == null)
            {
                Console.WriteLine("[DIAGNOSTICO] ❌ CH03/CH04 no encontrados");
                return (DeviceState.NoResponse, null);
            }

            // 3. Suscribir CH04
            var responseTcs = new TaskCompletionSource<byte[]>();
            await listener.SubscribeAsync(BleDeviceManager.CH04_UUID, ch04, data =>
            {
                responseTcs.TrySetResult(data);
            }, useIndicate: true);

            // 4. Enviar solo "RideOn" (6 bytes, sin sufijo ni pubkey)
            byte[] rideOnOnly = "RideOn"u8.ToArray();
            Console.WriteLine($"[DIAGNOSTICO] Enviando: {BitConverter.ToString(rideOnOnly).Replace("-", "")} ({rideOnOnly.Length}B)");

            var writer = new DataWriter();
            writer.WriteBytes(rideOnOnly);
            await ch03.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);

            // 5. Esperar respuesta (2 segundos)
            byte[]? response;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                response = await responseTcs.Task.WaitAsync(cts.Token);
            }
            catch (TimeoutException)
            {
                Console.WriteLine("[DIAGNOSTICO] ❌ Sin respuesta (timeout 2s) → dispositivo dormido o apagado");
                return (DeviceState.NoResponse, null);
            }

            Console.WriteLine($"[DIAGNOSTICO] Respuesta ({response.Length}B): {BitConverter.ToString(response).Replace("-", "")}");

            // 6. Clasificar respuesta
            if (response.Length == 6 && Encoding.ASCII.GetString(response) == "RideOn")
            {
                Console.WriteLine("[DIAGNOSTICO] 🔵 DISPOSITIVO DORMIDO (Sleeping)");
                Console.WriteLine("[DIAGNOSTICO]    Solo hace eco de 'RideOn' — no procesa handshakes.");
                Console.WriteLine("[DIAGNOSTICO]    Accion: Apaga y enciende el Click V2 (quita bateria 5s).");
                return (DeviceState.Sleeping_EchoOnly, response);
            }

            if (response.Length >= 72 && HandshakeParser.LooksLikeRejection(response))
            {
                Console.WriteLine("[DIAGNOSTICO] 🔒 DISPOSITIVO NO AUTORIZADO (Locked)");
                Console.WriteLine("[DIAGNOSTICO]    Responde con error estructurado antes del canal seguro.");
                Console.WriteLine("[DIAGNOSTICO]    Hipótesis: falta estado temporal de confianza/bonding.");
                return (DeviceState.Locked_ErrorResponse, response);
            }

            if (HandshakeParser.ExtractPublicKey(response) != null)
            {
                Console.WriteLine("[DIAGNOSTICO] ✅ DISPOSITIVO DESBLOQUEADO (Unlocked)");
                Console.WriteLine("[DIAGNOSTICO]    Contiene clave publica EC — listo para handshake ECDH.");
                return (DeviceState.Unlocked_PublicKey, response);
            }

            Console.WriteLine("[DIAGNOSTICO] ❓ Estado desconocido");
            return (DeviceState.Unknown, response);
        }
        finally
        {
            ble.Disconnect();
        }
    }

    public void Stop()
    {
        if (_stopped)
            return;
        _stopped = true;

        _keepAliveCts?.Cancel();
        _ble.Disconnect();
        _logger.Dispose();
    }

    public void PrintCryptoDiagnostics()
    {
        if (_cryptoV2 == null || !_cryptoV2.IsInitialized)
        {
            Console.WriteLine("[CRYPTO] Diagnóstico no disponible: sesión V2 no inicializada.");
            return;
        }

        Console.WriteLine("[CRYPTO] ═════════ DIAGNÓSTICO V2 ═════════");
        Console.WriteLine($"[CRYPTO] HKDF Info: {Encoding.ASCII.GetString(_cryptoV2.GetHkdfInfo())}");
        Console.WriteLine($"[CRYPTO] HKDF Salt Length: {_cryptoV2.HkdfSalt.Length} bytes");
        Console.WriteLine($"[CRYPTO] AES Key Length: {_cryptoV2.AesKey.Length} bytes");
        Console.WriteLine($"[CRYPTO] IV Base: {BitConverter.ToString(_cryptoV2.IvBase).Replace("-", " ")}");

        foreach (var candidate in GetAadCandidates())
        {
            try
            {
                byte[] probe = _cryptoV2.Encrypt("RideOn"u8.ToArray(), counter: 0, associatedData: candidate.Bytes);
                Console.WriteLine($"[CRYPTO] AAD {candidate.Name}: OK ({probe.Length}B)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CRYPTO] AAD {candidate.Name}: FAIL ({ex.Message})");
            }
        }
    }

    private static IEnumerable<(string Name, byte[]? Bytes)> GetAadCandidates()
    {
        yield return ("null", null);
        yield return ("empty", Array.Empty<byte>());
        yield return ("counter=0", new byte[] { 0x00, 0x00, 0x00, 0x00 });
        yield return ("CH03 UUID", BleDeviceManager.CH03_UUID.ToByteArray());
    }

    public void Dispose() => Stop();
}