using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using ZwiftClickV2.Bridge.Auth;
using ZwiftClickV2.Bridge.Crypto;

namespace ZwiftClickV2.Bridge.Tests;

public class UnlockChainTests
{
    // ── EcPoint ──────────────────────────────────────────────────────

    [Fact]
    public void EcPoint_Compress_ProducesValidPrefixAndLength()
    {
        byte[] raw64 = RawPubKey(out var p);
        byte[] compressed = EcPoint.Compress(raw64);

        Assert.Equal(33, compressed.Length);
        byte expectedPrefix = (byte)((p.Q.Y![31] & 0x01) == 1 ? 0x03 : 0x02);
        Assert.Equal(expectedPrefix, compressed[0]);
        Assert.Equal(p.Q.X!, compressed.AsSpan(1, 32).ToArray());
    }

    [Fact]
    public void EcPoint_CompressDecompress_RoundTrips()
    {
        byte[] raw64 = RawPubKey(out _);
        byte[] back = EcPoint.Decompress(EcPoint.Compress(raw64));
        Assert.Equal(raw64, back); // X y Y reconstruidos exactamente
    }

    // ── DeviceAuthChallenge (parser del reto del dispositivo) ─────────

    [Fact]
    public void DeviceAuthChallenge_TryParse_ExtractsVerbatimBody()
    {
        const ulong deviceId = 33752704; // = 0x2030680
        byte[] x32 = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        byte[] sig40 = Enumerable.Range(100, 40).Select(i => (byte)i).ToArray();

        // Trama tal cual la emite el device en CH02: FF 03 00 ‖ 0A 21 03 X 10 <varint> 1A 28 sig
        var proto = new List<byte> { 0x0A, 0x21, 0x03 };
        proto.AddRange(x32);
        proto.Add(0x10);
        proto.AddRange(Varint(deviceId));
        proto.Add(0x1A); proto.Add(40);
        proto.AddRange(sig40);
        byte[] body = proto.ToArray();

        byte[] frame = new byte[] { 0xFF, 0x03, 0x00 }.Concat(body).ToArray();

        Assert.True(DeviceAuthChallenge.TryParse(frame, out var ch));
        Assert.Equal(body, ch!.Body);                 // 82B contiguos, sin el header FF0300
        Assert.Equal(deviceId, ch.DeviceId);
        Assert.Equal(33, ch.DevicePublicKeyCompressed.Length);
        Assert.Equal(0x03, ch.DevicePublicKeyCompressed[0]);
        Assert.Equal(sig40, ch.Signature);
    }

    [Fact]
    public void DeviceAuthChallenge_TryParse_IgnoresNonChallengeFrames()
    {
        Assert.False(DeviceAuthChallenge.TryParse(new byte[] { 0xFF, 0x03, 0x00, 0x01, 0x02 }, out var ch));
        Assert.Null(ch);
        // Una notificación de batería plausible no debe parsearse como reto.
        Assert.False(DeviceAuthChallenge.TryParse(new byte[] { 0x23, 0x08, 0x00, 0x10, 0x64 }, out _));
    }

    // ── d-lock POST (verbatim + Bearer, sin Content-Type) ─────────────

    [Fact]
    public async Task DeviceUnlockClient_PostsBodyVerbatimWithBearer_On204()
    {
        byte[]? sentBody = null;
        string? authHeader = null;
        bool contentTypeSet = true;
        var handler = new StubHandler(async req =>
        {
            sentBody = await req.Content!.ReadAsByteArrayAsync();
            authHeader = req.Headers.Authorization?.ToString();
            contentTypeSet = req.Content!.Headers.ContentType != null;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        using var unlock = new DeviceUnlockClient(new HttpClient(handler));
        byte[] body = Enumerable.Range(1, 82).Select(i => (byte)i).ToArray();
        var decision = await new UnlockCoordinator(unlock).RunAsync("TESTTOKEN", body);

        Assert.True(decision.ShouldSendUnlockConfirm);
        Assert.Equal(204, decision.HttpStatusCode);
        Assert.Equal(body, sentBody);              // verbatim
        Assert.Equal("Bearer TESTTOKEN", authHeader);
        Assert.False(contentTypeSet);              // sin Content-Type
    }

    [Fact]
    public async Task UnlockCoordinator_NonAuthorizedIsNotConfirmed()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var unlock = new DeviceUnlockClient(new HttpClient(handler));

        var decision = await new UnlockCoordinator(unlock).RunAsync("T", new byte[82]);

        Assert.False(decision.ShouldSendUnlockConfirm);
        Assert.Equal(403, decision.HttpStatusCode);
    }

    // ── OAuth (login devuelve access_token) ───────────────────────────

    [Fact]
    public async Task ZwiftOAuthClient_Login_ReturnsAccessToken()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":\"AT123\",\"refresh_token\":\"RT\"}", Encoding.UTF8, "application/json")
        }));
        using var oauth = new ZwiftOAuthClient(new HttpClient(handler));

        string token = await oauth.LoginAsync(new ZwiftCredentials { Username = "u", Password = "p" });
        Assert.Equal("AT123", token);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static byte[] RawPubKey(out ECParameters p)
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        p = key.ExportParameters(false);
        byte[] raw = new byte[64];
        Array.Copy(p.Q.X!, 0, raw, 0, 32);
        Array.Copy(p.Q.Y!, 0, raw, 32, 32);
        return raw;
    }

    private static byte[] Varint(ulong v)
    {
        var bytes = new List<byte>();
        do { byte b = (byte)(v & 0x7F); v >>= 7; if (v != 0) b |= 0x80; bytes.Add(b); } while (v != 0);
        return bytes.ToArray();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => _responder(request);
    }
}
