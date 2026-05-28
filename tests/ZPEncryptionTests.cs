using System.Security.Cryptography;
using Xunit;
using ZwiftClickV2.Bridge.Crypto;

namespace ZwiftClickV2.Bridge.Tests;

/// <summary>
/// Tests para ZPEncryption V2 (Click 2025: GCM + salt 96B).
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
        bridgeZp.Initialize(bridgeKey, devicePub, saltPublicKey65: devicePub);

        var deviceZp = new ZPEncryptionV2();
        deviceZp.Initialize(deviceKey, bridgePub, saltPublicKey65: devicePub);

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

        var az = new ZPEncryptionV2(); az.Initialize(a, bPub, saltPublicKey65: bPub);
        byte[] ct = az.Encrypt("test"u8.ToArray());
        ct[0] ^= 0xFF;
        var bz = new ZPEncryptionV2(); bz.Initialize(b, aPub, saltPublicKey65: bPub);
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
    }

    [Fact]
    public void Factory_DetectsFromResponse()
    {
        byte[] resp = new byte[] { (byte)'R', (byte)'i', (byte)'d', (byte)'e', (byte)'O', (byte)'n', 0x02, 0x03, 0x10, 0x64 };
        Assert.Equal(ZPEncryptionFactory.ProtocolVersion.V2,
            ZPEncryptionFactory.DetectVersionFromResponse(resp));
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