using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using ZwiftClickV2.Bridge.Auth;
using ZwiftClickV2.Bridge.Crypto;

namespace ZwiftClickV2.Bridge.Tests;

public class UnlockChainTests
{
    [Fact]
    public void EcPoint_Compress_ProducesValidPrefixAndLength()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(false);
        byte[] raw64 = new byte[64];
        Array.Copy(p.Q.X!, 0, raw64, 0, 32);
        Array.Copy(p.Q.Y!, 0, raw64, 32, 32);

        byte[] compressed = EcPoint.Compress(raw64);

        Assert.Equal(33, compressed.Length);
        Assert.True(compressed[0] == 0x02 || compressed[0] == 0x03);
        byte expectedPrefix = (byte)((p.Q.Y![31] & 0x01) == 1 ? 0x03 : 0x02);
        Assert.Equal(expectedPrefix, compressed[0]);
        Assert.Equal(p.Q.X!, compressed.AsSpan(1, 32).ToArray());
    }

    [Fact]
    public void DeviceAuthChallenge_ToProtobuf_EncodesThreeFields()
    {
        const ulong deviceId = 33752704; // = 0x2030680, el id del ejemplo capturado
        byte[] pub64 = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();
        byte[] sig40 = Enumerable.Range(100, 40).Select(i => (byte)i).ToArray();
        var challenge = DeviceAuthChallenge.FromDevicePublicKey(pub64, deviceId, signature40: sig40);

        byte[] body = challenge.ToProtobuf();

        // Campo 1: tag 0x0A, len 33 (pubkey comprimida)
        Assert.Equal(0x0A, body[0]);
        Assert.Equal(33, body[1]);
        // Campo 2: tag 0x10 (varint) tras 2 + 33 bytes; se decodifica de vuelta y se compara
        int field2Tag = 2 + 33;
        Assert.Equal(0x10, body[field2Tag]);
        var (value, next) = ReadVarint(body, field2Tag + 1);
        Assert.Equal(deviceId, value);
        // Campo 3: tag 0x1A, len 40
        Assert.Equal(0x1A, body[next]);
        Assert.Equal(40, body[next + 1]);
        Assert.Equal(sig40, body.AsSpan(next + 2, 40).ToArray());
    }

    [Fact]
    public void DeviceAuthChallenge_RejectsWrongSignatureLength()
        => Assert.Throws<ArgumentException>(() =>
            DeviceAuthChallenge.FromDevicePublicKey(new byte[64], 1, new byte[39]));

    [Fact]
    public async Task UnlockCoordinator_Returns204Authorization()
    {
        var oauthHandler = new StubHandler((req) =>
        {
            string json = "{\"access_token\":\"TESTTOKEN\",\"token_type\":\"bearer\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        string? sentAuthHeader = null;
        var dlockHandler = new StubHandler((req) =>
        {
            sentAuthHeader = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        using var oauth = new ZwiftOAuthClient(new HttpClient(oauthHandler));
        using var unlock = new DeviceUnlockClient(new HttpClient(dlockHandler));
        var coordinator = new UnlockCoordinator(oauth, unlock);

        var challenge = DeviceAuthChallenge.FromDevicePublicKey(
            Enumerable.Range(1, 64).Select(i => (byte)i).ToArray(),
            42,
            Enumerable.Range(1, 40).Select(i => (byte)i).ToArray());
        var creds = new ZwiftCredentials { Username = "me@example.com", Password = "x" };

        var decision = await coordinator.RunAsync(creds, challenge);

        Assert.True(decision.ShouldSendUnlockConfirm);
        Assert.Equal(204, decision.HttpStatusCode);
        Assert.Equal("Bearer TESTTOKEN", sentAuthHeader);
    }

    [Fact]
    public async Task UnlockCoordinator_NonAuthorizedIsNotConfirmed()
    {
        var oauthHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":\"T\"}", Encoding.UTF8, "application/json")
        });
        var dlockHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        using var oauth = new ZwiftOAuthClient(new HttpClient(oauthHandler));
        using var unlock = new DeviceUnlockClient(new HttpClient(dlockHandler));
        var coordinator = new UnlockCoordinator(oauth, unlock);

        var challenge = DeviceAuthChallenge.FromDevicePublicKey(
            new byte[64], 1, new byte[40]);
        var creds = new ZwiftCredentials { Username = "u", Password = "p" };

        var decision = await coordinator.RunAsync(creds, challenge);

        Assert.False(decision.ShouldSendUnlockConfirm);
        Assert.Equal(403, decision.HttpStatusCode);
    }

    private static (ulong value, int next) ReadVarint(byte[] data, int offset)
    {
        ulong value = 0;
        int shift = 0;
        int i = offset;
        while (true)
        {
            byte b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return (value, i);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
