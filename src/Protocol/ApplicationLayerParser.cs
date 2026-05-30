using ZwiftClickV2.Bridge.Crypto;

namespace ZwiftClickV2.Bridge.Protocol;

public enum ZapChannel
{
    Unknown = 0,
    CH02_AsyncNotify,
    CH03_SyncRx,
    CH04_SyncTx,
    CH100_CtrlA,
    CH101_CtrlB,
    CH102_Bcast
}

public class ParsedZapMessage
{
    public ZapChannel Channel { get; init; }
    public byte ApplicationOpcode { get; init; }
    public string SymbolicName { get; init; } = "UNKNOWN";
    public byte[] Plaintext { get; init; } = Array.Empty<byte>();
    public bool WasEncrypted { get; init; }
    public uint? Counter { get; init; }
    public byte[]? RawTag { get; init; }
}

/// <summary>
/// Parser de capa de aplicación que enruta por handle ATT origen.
/// </summary>
public class ApplicationLayerParser
{
    public const ushort HandleCH02 = 0x001B;
    public const ushort HandleCH03 = 0x001E;
    public const ushort HandleCH04 = 0x0020;
    public const ushort HandleCH100 = 0x0023;
    public const ushort HandleCH101 = 0x0027;
    public const ushort HandleCH102 = 0x002B;

    private readonly IZPEncryption? _crypto;
    private readonly ZopSequencer? _sequencer;

    public ApplicationLayerParser(IZPEncryption? crypto = null, ZopSequencer? sequencer = null)
    {
        _crypto = crypto;
        _sequencer = sequencer;
    }

    public static ZapChannel MapChannel(ushort handle)
    {
        return handle switch
        {
            HandleCH02 => ZapChannel.CH02_AsyncNotify,
            HandleCH03 => ZapChannel.CH03_SyncRx,
            HandleCH04 => ZapChannel.CH04_SyncTx,
            HandleCH100 => ZapChannel.CH100_CtrlA,
            HandleCH101 => ZapChannel.CH101_CtrlB,
            HandleCH102 => ZapChannel.CH102_Bcast,
            _ => ZapChannel.Unknown
        };
    }

    public ParsedZapMessage Parse(ushort handle, byte[] payload)
    {
        var channel = MapChannel(handle);

        // CH03 es canal host->device; se conserva para auditoria.
        if (channel == ZapChannel.CH03_SyncRx)
        {
            return BuildPlain(channel, payload, "HOST_TX_AUDIT");
        }

        if (channel == ZapChannel.CH02_AsyncNotify ||
            channel == ZapChannel.CH04_SyncTx ||
            channel == ZapChannel.CH100_CtrlA ||
            channel == ZapChannel.CH101_CtrlB ||
            channel == ZapChannel.CH102_Bcast)
        {
            if (TryDecrypt(channel, payload, out var decrypted))
            {
                return decrypted;
            }

            // En CH100/101/102 preservar raw para forensics cuando falle el decrypt.
            if (channel == ZapChannel.CH100_CtrlA ||
                channel == ZapChannel.CH101_CtrlB ||
                channel == ZapChannel.CH102_Bcast)
            {
                return BuildPlain(channel, payload, "RAW_FOR_ANALYSIS");
            }
        }

        return BuildPlain(channel, payload, "UNKNOWN_OR_UNDECRYPTED");
    }

    private bool TryDecrypt(ZapChannel channel, byte[] payload, out ParsedZapMessage message)
    {
        message = default!;

        if (_crypto == null || !_crypto.IsInitialized)
            return false;

        // Formato preferido: frame ZOP [seq LE 4B][ciphertext+tag].
        if (_sequencer != null && payload.Length >= 8)
        {
            try
            {
                var (seq, plaintext) = _sequencer.UnpackAndDecrypt(payload);
                message = BuildEncrypted(channel, plaintext, seq, payload.AsSpan(payload.Length - 4, 4).ToArray());
                return true;
            }
            catch
            {
                // Fallback: algunos canales pueden entregar solo [ciphertext][tag].
            }
        }

        if (payload.Length < 4)
            return false;

        try
        {
            byte[] plaintext = _crypto.Decrypt(payload);
            message = BuildEncrypted(channel, plaintext, null, payload.AsSpan(payload.Length - 4, 4).ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ParsedZapMessage BuildEncrypted(ZapChannel channel, byte[] plaintext, uint? counter, byte[]? tag)
    {
        byte opcode = plaintext.Length > 0 ? plaintext[0] : (byte)0x00;
        return new ParsedZapMessage
        {
            Channel = channel,
            ApplicationOpcode = opcode,
            SymbolicName = SymbolicOpcode(opcode),
            Plaintext = plaintext,
            WasEncrypted = true,
            Counter = counter,
            RawTag = tag
        };
    }

    private static ParsedZapMessage BuildPlain(ZapChannel channel, byte[] payload, string symbolicName)
    {
        byte opcode = payload.Length > 0 ? payload[0] : (byte)0x00;
        return new ParsedZapMessage
        {
            Channel = channel,
            ApplicationOpcode = opcode,
            SymbolicName = symbolicName,
            Plaintext = payload,
            WasEncrypted = false,
            Counter = null,
            RawTag = payload.Length >= 4 ? payload.AsSpan(payload.Length - 4, 4).ToArray() : null
        };
    }

    // Nombres según el catálogo autoritativo del decompile (docs/protocol/opcode-catalog.md).
    public static string SymbolicOpcode(byte opcode)
    {
        return opcode switch
        {
            0x04 => "TRAINER_NOTIF",
            0x07 => "TRAINER_CONFIG_STATUS",
            0x08 => "ZWIFT_PLAY_NOTIF",
            0x15 => "CONTROLLER_REQUEST",
            0x19 => "RESET",
            0x23 => "BATTERY_STATUS",
            0x28 => "CONTROLLER_NOTIFICATION",
            0x37 => "ZWIFT_PLAY_DEVICE_STATUS",
            0x38 => "ZWIFT_CLICK_NOTIFICATION",
            0xFF => "LOST_CONTROL",
            _ => $"UNKNOWN_0x{opcode:X2}"
        };
    }
}
