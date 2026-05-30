namespace ZwiftClickV2.Bridge.Auth;

/// <summary>
/// Resultado de la fase server-backed del unlock.
/// </summary>
/// <param name="ShouldSendUnlockConfirm">
/// true ⇒ el servidor autorizó (204) y el bridge debe escribir <c>FF 04 00</c> en CH03.
/// </param>
public sealed record UnlockDecision(bool ShouldSendUnlockConfirm, int HttpStatusCode, string Notes);

/// <summary>
/// Envuelve la llamada al d-lock-service en una decisión de unlock. El token ya está resuelto
/// (login/refresh) y el cuerpo del reto ya fue capturado del dispositivo por BLE; aquí solo se hace
/// el POST verbatim y se decide si procede escribir <c>FF 04 00</c>. Thin y testeable sin hardware.
/// </summary>
public sealed class UnlockCoordinator
{
    private readonly DeviceUnlockClient _unlock;

    public UnlockCoordinator(DeviceUnlockClient unlock)
    {
        _unlock = unlock;
    }

    public async Task<UnlockDecision> RunAsync(
        string accessToken,
        byte[] challengeBody,
        CancellationToken cancellationToken = default)
    {
        var result = await _unlock.AuthenticateAsync(accessToken, challengeBody, cancellationToken);
        return new UnlockDecision(result.Authorized, result.HttpStatusCode, result.Notes);
    }
}
