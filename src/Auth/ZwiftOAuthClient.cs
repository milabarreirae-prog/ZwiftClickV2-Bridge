using System.Net.Http.Headers;
using System.Text.Json;

namespace ZwiftClickV2.Bridge.Auth;

/// <summary>
/// Cliente OAuth contra el endpoint de token Keycloak de Zwift. Inicia sesión con la cuenta
/// DEL PROPIO USUARIO y devuelve un <c>access_token</c> que solo vive en memoria.
///
/// El flujo OAuth completo (endpoint Keycloak <c>secure.zwift.com</c>) fue confirmado por la
/// captura del equipo de investigación. Esta herramienta nunca embebe ni persiste el token.
/// </summary>
public sealed class ZwiftOAuthClient : IDisposable
{
    // Endpoint de token Keycloak de Zwift (realm "zwift").
    private const string TokenEndpoint =
        "https://secure.zwift.com/auth/realms/zwift/protocol/openid-connect/token";

    // client_id público usado por los clientes oficiales de Zwift para el grant de password.
    private const string ClientId = "Zwift_Mobile_Link";

    private readonly HttpClient _http;

    public ZwiftOAuthClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    /// <summary>
    /// Hace login con las credenciales del usuario y devuelve un <c>access_token</c>.
    /// Lanza <see cref="HttpRequestException"/> si Zwift rechaza el login.
    /// </summary>
    public async Task<string> LoginAsync(ZwiftCredentials credentials, CancellationToken cancellationToken = default)
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", ClientId),
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("username", credentials.Username),
            new KeyValuePair<string, string>("password", credentials.Password),
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = form };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Login Zwift falló ({(int)response.StatusCode}): revisa usuario/contraseña.");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var token) ||
            token.ValueKind != JsonValueKind.String)
        {
            throw new HttpRequestException("La respuesta de Zwift no contiene access_token.");
        }

        return token.GetString()!;
    }

    public void Dispose() => _http.Dispose();
}
