using System.Net;
using System.Net.Http.Headers;

namespace ZwiftClickV2.Bridge.Auth;

/// <summary>
/// Resultado de la validación de unlock server-backed.
/// </summary>
public sealed record DeviceUnlockResult(bool Authorized, int HttpStatusCode, string Notes);

/// <summary>
/// Cliente del servicio de DRM de Zwift (<c>d-lock-service</c>). El unlock del Click V2 es
/// SERVER-BACKED: requiere el <c>access_token</c> de la cuenta del usuario. El cuerpo es el blob de
/// 82B que **genera el dispositivo** (campos 1/2/3), reenviado **VERBATIM** — el bridge no construye
/// ni firma nada. El servidor responde <c>204 No Content</c>: validación "¿esta cuenta puede usar
/// este dispositivo?". Tras el 204, el unlock BLE se completa escribiendo <c>FF 04 00</c> en CH03.
///
/// Flujo confirmado — ver docs/protocol/unlock-flow.md.
/// </summary>
public sealed class DeviceUnlockClient : IDisposable
{
    private const string AuthenticateEndpoint =
        "https://us-or-rly101.zwift.com/api/d-lock-service/device/authenticate";

    private readonly HttpClient _http;

    public DeviceUnlockClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// POST del cuerpo del reto (82B, verbatim) con <c>Authorization: Bearer &lt;accessToken&gt;</c>
    /// y **sin Content-Type** (igual que la app oficial). Autorizado = <c>204 No Content</c>.
    /// </summary>
    public async Task<DeviceUnlockResult> AuthenticateAsync(
        string accessToken,
        byte[] challengeBody,
        CancellationToken cancellationToken = default)
    {
        using var content = new ByteArrayContent(challengeBody);
        content.Headers.ContentType = null; // la app oficial no fija Content-Type

        using var request = new HttpRequestMessage(HttpMethod.Post, AuthenticateEndpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, cancellationToken);
        int status = (int)response.StatusCode;

        bool authorized = response.StatusCode == HttpStatusCode.NoContent || response.IsSuccessStatusCode;
        string notes = authorized
            ? $"Servidor autorizó el dispositivo (HTTP {status}). Procede el unlock BLE (FF 04 00)."
            : $"El servidor no autorizó (HTTP {status}). Token inválido/expirado o la cuenta no posee este dispositivo.";

        return new DeviceUnlockResult(authorized, status, notes);
    }

    public void Dispose() => _http.Dispose();
}
