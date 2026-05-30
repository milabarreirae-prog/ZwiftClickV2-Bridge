using System.Net;
using System.Net.Http.Headers;

namespace ZwiftClickV2.Bridge.Auth;

/// <summary>
/// Resultado de la validación de unlock server-backed.
/// </summary>
public sealed record DeviceUnlockResult(bool Authorized, int HttpStatusCode, string Notes);

/// <summary>
/// Cliente del servicio de DRM de Zwift (<c>d-lock-service</c>). El unlock del Click V2 es
/// SERVER-BACKED: requiere el <c>access_token</c> de la cuenta del usuario y una validación del
/// servidor. El servidor responde <c>204 No Content</c> (no devuelve un ticket reinyectable):
/// es validación "¿esta cuenta puede usar este dispositivo?". Tras el 204, el unlock BLE se
/// completa escribiendo <c>FF 04 00</c> en CH03.
///
/// Flujo confirmado — ver docs/protocol/phaseC-dlock-auth.md.
/// </summary>
public sealed class DeviceUnlockClient : IDisposable
{
    private const string AuthenticateEndpoint =
        "https://us-or-rly101.zwift.com/api/d-lock-service/device/authenticate";

    private readonly HttpClient _http;

    public DeviceUnlockClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    /// <summary>
    /// POST del challenge del dispositivo con <c>Authorization: Bearer &lt;accessToken&gt;</c>.
    /// Devuelve autorizado = true sólo ante un <c>204 No Content</c>.
    /// </summary>
    public async Task<DeviceUnlockResult> AuthenticateAsync(
        string accessToken,
        DeviceAuthChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        byte[] body = challenge.ToProtobuf();

        using var request = new HttpRequestMessage(HttpMethod.Post, AuthenticateEndpoint)
        {
            Content = new ByteArrayContent(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, cancellationToken);
        int status = (int)response.StatusCode;

        bool authorized = response.StatusCode == HttpStatusCode.NoContent; // 204
        string notes = authorized
            ? "Servidor autorizó el dispositivo (204). Procede el unlock BLE (FF 04 00)."
            : $"El servidor no autorizó (HTTP {status}). El dispositivo seguirá bloqueado.";

        return new DeviceUnlockResult(authorized, status, notes);
    }

    public void Dispose() => _http.Dispose();
}
