using System.Net.Http.Headers;
using System.Text.Json;

namespace ZwiftClickV2.Bridge.Auth;

/// <summary>
/// Login contra el realm Keycloak de Zwift. Inicia sesión con la cuenta DEL PROPIO USUARIO y
/// devuelve un <c>access_token</c> (Bearer) que solo vive en memoria — nunca se embebe ni se loguea.
///
/// Endpoint (descubierto de tráfico capturado, `GET …/api/auth` → realm "zwift"):
///   POST https://secure.zwift.com/auth/realms/zwift/protocol/openid-connect/token
///
/// Grants verificados en vivo (ver docs/protocol/zwift-login.md):
///   • password (ROPC): client_id <c>Zwift_Mobile_Link</c> (tiene Direct Access Grants).
///   • refresh_token:   client_id <c>Game_Launcher</c> (sin contraseña; útil con 2FA).
/// </summary>
public sealed class ZwiftOAuthClient : IDisposable
{
    public const string TokenEndpoint =
        "https://secure.zwift.com/auth/realms/zwift/protocol/openid-connect/token";

    /// <summary>Cliente público con Direct Access Grants (password grant).</summary>
    public const string MobileClientId = "Zwift_Mobile_Link";

    /// <summary>Cliente del launcher (refresh_token / authorization_code grants).</summary>
    public const string LauncherClientId = "Game_Launcher";

    private readonly HttpClient _http;

    public ZwiftOAuthClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Login ROPC con usuario + contraseña (client_id Zwift_Mobile_Link).</summary>
    public Task<string> LoginAsync(ZwiftCredentials credentials, CancellationToken ct = default)
        => LoginAsync(credentials.Username, credentials.Password, MobileClientId, ct);

    public Task<string> LoginAsync(string username, string password, string clientId = MobileClientId, CancellationToken ct = default)
        => PostFormAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
        }, $"password login ({clientId})", ct);

    /// <summary>Intercambia un refresh_token por un access_token fresco (sin contraseña).</summary>
    public Task<string> RefreshAsync(string refreshToken, string clientId = LauncherClientId, CancellationToken ct = default)
        => PostFormAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, $"token refresh ({clientId})", ct);

    /// <summary>
    /// Resuelve un access_token desde el entorno, en orden:
    ///   1. ZWIFT_ACCESS_TOKEN              — token ya en mano
    ///   2. ZWIFT_USERNAME + ZWIFT_PASSWORD — password grant
    ///   3. ZWIFT_REFRESH_TOKEN             — refresh grant
    /// Devuelve null si no hay nada configurado.
    /// </summary>
    public async Task<string?> ResolveTokenFromEnvAsync(CancellationToken ct = default)
    {
        string? at = Environment.GetEnvironmentVariable("ZWIFT_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(at)) return at;

        string? user = Environment.GetEnvironmentVariable("ZWIFT_USERNAME");
        string? pass = Environment.GetEnvironmentVariable("ZWIFT_PASSWORD");
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
            return await LoginAsync(user, pass, MobileClientId, ct);

        string? rt = Environment.GetEnvironmentVariable("ZWIFT_REFRESH_TOKEN");
        if (!string.IsNullOrWhiteSpace(rt))
            return await RefreshAsync(rt, LauncherClientId, ct);

        return null;
    }

    private async Task<string> PostFormAsync(Dictionary<string, string> form, string what, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync(TokenEndpoint, content, ct);
        string raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Zwift {what} falló: HTTP {(int)resp.StatusCode}.");

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String)
            throw new HttpRequestException("La respuesta de Zwift no contiene access_token.");

        return token.GetString()!;
    }

    public void Dispose() => _http.Dispose();
}
