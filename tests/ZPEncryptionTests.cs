using System.Security.Cryptography;
using Xunit;
using ZwiftClickV2.Bridge.Crypto;
using ZwiftClickV2.Bridge.Protocol;

namespace ZwiftClickV2.Bridge.Tests;

/// <summary>
/// Tests para ZPEncryption V2 y helpers de protocolo Click 2025.
/// </summary>
public class ZPEncryptionTests
{
    [Fact]
    public void V2_Initialize_RejectsKeyWithout04Prefix()
    {
        var zp = new ZPEncryptionV2();
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ex = Assert.Throws<ArgumentException>(() => zp.Initialize(key, new byte[65]));
        Assert.Contains("0x04", ex.Message);
    }

    [Fact]
    public void V2_Initialize_RejectsWrongSize()
    {
        var zp = new ZPEncryptionV2();
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<ArgumentException>(() => zp.Initialize(key, new byte[33]));
    }

    [Fact]
    public void V2_RoundTrip_Works()
    {
        using var bridgeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var deviceKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] bridgePub = ExportPub(bridgeKey);
        byte[] devicePub = ExportPub(deviceKey);

        var bridgeZp = new ZPEncryptionV2();
        bridgeZp.Initialize(bridgeKey, devicePub, devicePub, bridgePub);

        var deviceZp = new ZPEncryptionV2();
        deviceZp.Initialize(deviceKey, bridgePub, devicePub, bridgePub);

        byte[] msg = "Hola"u8.ToArray();
        byte[] ct = bridgeZp.Encrypt(msg);
        Assert.True(ct.Length >= msg.Length + 4);
        Assert.Equal(msg, deviceZp.Decrypt(ct));
    }

    [Fact]
    public void V2_TagDetectsCorruption()
    {
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] bPub = ExportPub(b), aPub = ExportPub(a);

        var az = new ZPEncryptionV2(); az.Initialize(a, bPub, bPub, aPub);
        byte[] ct = az.Encrypt("test"u8.ToArray());
        ct[0] ^= 0xFF;
        var bz = new ZPEncryptionV2(); bz.Initialize(b, aPub, bPub, aPub);
        Assert.ThrowsAny<CryptographicException>(() => bz.Decrypt(ct));
    }

    [Fact]
    public void V2_CountersProduceDifferentOutputs()
    {
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var zp = new ZPEncryptionV2(); zp.Initialize(a, ExportPub(b));
        byte[] m = "AAAA"u8.ToArray();
        Assert.NotEqual(zp.Encrypt(m), zp.Encrypt(m));
    }

    [Fact]
    public void V2_ThrowsIfNotInitialized()
    {
        var zp = new ZPEncryptionV2();
        Assert.False(zp.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => zp.Encrypt([1, 2, 3]));
        Assert.Throws<InvalidOperationException>(() => zp.Decrypt([1, 2, 3, 4, 5, 6]));
    }

    [Fact]
    public void V2_IsInitialized_ReflectsState()
    {
        var zp = new ZPEncryptionV2();
        Assert.False(zp.IsInitialized);
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        zp.Initialize(a, ExportPub(b));
        Assert.True(zp.IsInitialized);
    }

    [Fact]
    public void V2_UsesExpectedHkdfParameters()
    {
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var zp = new ZPEncryptionV2();
        zp.Initialize(a, ExportPub(b));

        Assert.Equal("handshake data", System.Text.Encoding.ASCII.GetString(zp.GetHkdfInfo()));
        Assert.Equal(128, zp.HkdfSalt.Length);
        Assert.Equal(32, zp.AesKey.Length);
        Assert.Equal(4, zp.IvBase.Length);
    }

    [Fact]
    public void ZapCrypto_NullAad_RoundTrip_Works()
    {
        byte[] devicePub = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();
        byte[] localPub = Enumerable.Range(65, 64).Select(i => (byte)i).ToArray();
        byte[] sharedSecret = Enumerable.Range(129, 32).Select(i => (byte)i).ToArray();

        var crypto = new ZapCrypto();
        crypto.DeriveSessionKey(devicePub, localPub, sharedSecret);

        byte[] ciphertext = crypto.Encrypt("RideOn"u8.ToArray(), counter: 1, associatedData: null);
        byte[] plaintext = crypto.Decrypt(ciphertext, counter: 1, associatedData: null);

        Assert.Equal("RideOn"u8.ToArray(), plaintext);
        Assert.Equal(128, crypto.HkdfSalt.Length);
        Assert.Equal(8, crypto.BuildNonce(1).Length);
    }

    [Fact]
    public void V1_RoundTrip_Works()
    {
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var az = new ZPEncryptionV1(); az.Initialize(a, ExportPub(b));
        var bz = new ZPEncryptionV1(); bz.Initialize(b, ExportPub(a));
        byte[] msg = "V1 test"u8.ToArray();
        Assert.Equal(msg, bz.Decrypt(az.Encrypt(msg)));
    }

    [Fact]
    public void Factory_DetectsV1()
    {
        Assert.Equal(ZPEncryptionFactory.ProtocolVersion.V1,
            ZPEncryptionFactory.DetectVersion(new byte[] { 0x01, 0x01 }));
        Assert.Equal(ZPEncryptionFactory.ProtocolVersion.V1,
            ZPEncryptionFactory.DetectVersion(new byte[] { 0x00, 0x09 }));
    }

    [Fact]
    public void Factory_DetectsV2()
    {
        Assert.Equal(ZPEncryptionFactory.ProtocolVersion.V2,
            ZPEncryptionFactory.DetectVersion(new byte[] { 0x02, 0x03 }));
        Assert.Equal(ZPEncryptionFactory.ProtocolVersion.V2,
            ZPEncryptionFactory.DetectVersion(new byte[] { 0x01, 0x02 }));
        Assert.Equal(ZPEncryptionFactory.ProtocolVersion.V2,
            ZPEncryptionFactory.DetectVersion(new byte[] { 0x01, 0x03 }));
    }

    [Fact]
    public void Factory_DetectsFromResponse()
    {
        byte[] resp = new byte[] { (byte)'R', (byte)'i', (byte)'d', (byte)'e', (byte)'O', (byte)'n', 0x02, 0x03, 0x10, 0x64 };
        Assert.Equal(ZPEncryptionFactory.ProtocolVersion.V2,
            ZPEncryptionFactory.DetectVersionFromResponse(resp));
    }

    [Fact]
    public void HandshakeParser_ExtractsRawPublicKey_From_0103_Response()
    {
        byte[] raw = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();
        byte[] response = new byte[72];
        Array.Copy("RideOn"u8.ToArray(), response, 6);
        response[6] = 0x01;
        response[7] = 0x03;
        Array.Copy(raw, 0, response, 8, 64);

        Assert.Equal(HandshakeParser.ResponseType.PublicKey, HandshakeParser.Classify(response));
        Assert.Equal(raw, HandshakeParser.ExtractRawPublicKey(response));

        byte[] prefixed = HandshakeParser.ExtractPublicKey(response)!;
        Assert.Equal(65, prefixed.Length);
        Assert.Equal(0x04, prefixed[0]);
        Assert.Equal(raw, prefixed.Skip(1).ToArray());
    }

    [Fact]
    public void HandshakeParser_Detects_Rejection_Response()
    {
        byte[] rejection = new byte[]
        {
            (byte)'R', (byte)'i', (byte)'d', (byte)'e', (byte)'O', (byte)'n',
            0x02, 0x03, 0x58, 0x02, 0x00, 0x00
        };

        Assert.True(HandshakeParser.LooksLikeRejection(rejection));
        Assert.Equal(HandshakeParser.ResponseType.StatusMessage, HandshakeParser.Classify(rejection));
    }

    private static byte[] ExportPub(ECDiffieHellman ecdh)
    {
        var p = ecdh.ExportParameters(false);
        byte[] k = new byte[65]; k[0] = 0x04;
        Array.Copy(p.Q.X!, 0, k, 1, 32);
        Array.Copy(p.Q.Y!, 0, k, 33, 32);
        return k;
    }
}