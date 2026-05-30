namespace ZwiftClickV2.Bridge.Auth;

/// <summary>
/// Resultado de la fase server-backed del unlock.
/// </summary>
/// <param name="ShouldSendUnlockConfirm">
/// true ⇒ el servidor autorizó (204) y el bridge debe escribir <c>FF 04 00</c> en CH03.
/// </param>
public sealed record UnlockDecision(bool ShouldSendUnlockConfirm, int? HttpStatusCode, string Notes);

/// <summary>
/// Orquesta la parte de red del unlock del Click V2 (la única forma de salir del bloqueo DRM):
///   1. Login OAuth con la cuenta Zwift del usuario → access_token (solo en memoria).
///   2. POST del challenge del dispositivo a d-lock-service con Bearer → 204.
///   3. Si 204 ⇒ el bridge escribe <c>FF 04 00</c> por BLE.
///
/// No mantiene estado BLE: recibe el challenge ya armado y devuelve la decisión. Esto lo hace
/// testeable sin hardware ni red real (inyectando los clientes).
/// </summary>
public sealed class UnlockCoordinator
{
    private readonly ZwiftOAuthClient _oauth;
    private readonly DeviceUnlockClient _unlock;

    public UnlockCoordinator(ZwiftOAuthClient oauth, DeviceUnlockClient unlock)
    {
        _oauth = oauth;
        _unlock = unlock;
    }

    public async Task<UnlockDecision> RunAsync(
        ZwiftCredentials credentials,
        DeviceAuthChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        string accessToken = await _oauth.LoginAsync(credentials, cancellationToken);

        var result = await _unlock.AuthenticateAsync(accessToken, challenge, cancellationToken);
        return new UnlockDecision(result.Authorized, result.HttpStatusCode, result.Notes);
    }
}
