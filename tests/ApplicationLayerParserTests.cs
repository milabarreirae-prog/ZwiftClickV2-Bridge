using System.Security.Cryptography;
using ZwiftClickV2.Bridge.Crypto;
using ZwiftClickV2.Bridge.Protocol;

namespace ZwiftClickV2.Bridge.Tests;

public class ApplicationLayerParserTests
{
    [Fact]
    public void MapChannel_ReturnsExpectedChannel()
    {
        Assert.Equal(ZapChannel.CH02_AsyncNotify, ApplicationLayerParser.MapChannel(0x001B));
        Assert.Equal(ZapChannel.CH03_SyncRx, ApplicationLayerParser.MapChannel(0x001E));
        Assert.Equal(ZapChannel.CH04_SyncTx, ApplicationLayerParser.MapChannel(0x0020));
        Assert.Equal(ZapChannel.CH100_CtrlA, ApplicationLayerParser.MapChannel(0x0023));
        Assert.Equal(ZapChannel.CH101_CtrlB, ApplicationLayerParser.MapChannel(0x0027));
        Assert.Equal(ZapChannel.CH102_Bcast, ApplicationLayerParser.MapChannel(0x002B));
    }

    [Fact]
    public void Parse_CH03_AuditsWithoutDecrypt()
    {
        var parser = new ApplicationLayerParser();

        var parsed = parser.Parse(0x001E, new byte[] { 0xAA, 0xBB, 0xCC });

        Assert.Equal(ZapChannel.CH03_SyncRx, parsed.Channel);
        Assert.False(parsed.WasEncrypted);
        Assert.Equal("HOST_TX_AUDIT", parsed.SymbolicName);
        Assert.Equal((byte)0xAA, parsed.ApplicationOpcode);
    }

    [Fact]
    public void Parse_CH02_DecryptsFrameAndReadsOpcode()
    {
        var crypto = new FakeEncryption
        {
            IsInitializedValue = true,
            NextDecryptResult = new byte[] { 0x38, 0x02, 0x10 }
        };
        var sequencer = new ZopSequencer(crypto);
        var parser = new ApplicationLayerParser(crypto, sequencer);

        byte[] frame = new byte[]
        {
            0x05, 0x00, 0x00, 0x00,
            0xAA, 0xBB, 0xCC, 0xDD
        };

        var parsed = parser.Parse(0x001B, frame);

        Assert.True(parsed.WasEncrypted);
        Assert.Equal((uint)5, parsed.Counter);
        Assert.Equal((byte)0x38, parsed.ApplicationOpcode);
        Assert.Equal("ZWIFT_CLICK_NOTIFICATION", parsed.SymbolicName);
    }

    [Fact]
    public void Parse_CH100_FallsBackToRawWhenDecryptFails()
    {
        var crypto = new FakeEncryption
        {
            IsInitializedValue = true,
            ThrowOnDecrypt = true
        };
        var parser = new ApplicationLayerParser(crypto, new ZopSequencer(crypto));

        byte[] payload = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
        var parsed = parser.Parse(0x0023, payload);

        Assert.Equal(ZapChannel.CH100_CtrlA, parsed.Channel);
        Assert.False(parsed.WasEncrypted);
        Assert.Equal("RAW_FOR_ANALYSIS", parsed.SymbolicName);
        Assert.Equal(payload, parsed.Plaintext);
    }

    private sealed class FakeEncryption : IZPEncryption
    {
        public bool IsInitializedValue { get; set; }
        public bool ThrowOnDecrypt { get; set; }
        public byte[] NextDecryptResult { get; set; } = Array.Empty<byte>();

        public bool IsInitialized => IsInitializedValue;

        public byte[] Decrypt(byte[] ciphertextWithTag)
        {
            if (ThrowOnDecrypt)
                throw new CryptographicException("test decrypt fail");
            return NextDecryptResult.Length == 0 ? ciphertextWithTag : NextDecryptResult;
        }

        public byte[] Encrypt(byte[] plaintext) => plaintext;
        public void InitializeV1(ECDiffieHellman ourKey, byte[] peerPublicKey65) { }
        public void InitializeV2(ECDiffieHellman ourKey, byte[] peerPublicKey65, byte[]? saltPublicKey65 = null) { }
        public void ResetCounters() { }
    }
}
