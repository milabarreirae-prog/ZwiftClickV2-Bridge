namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Cliente de auth para pruebas/replay: siempre retorna un payload fijo.
/// </summary>
public sealed class ReplayAuthChallengeClient : IAuthChallengeClient
{
    private readonly byte[] _responsePayload;
    private readonly string _source;

    public ReplayAuthChallengeClient(byte[] responsePayload, string source)
    {
        _responsePayload = responsePayload ?? throw new ArgumentNullException(nameof(responsePayload));
        _source = string.IsNullOrWhiteSpace(source) ? "replay" : source;
    }

    public Task<AuthChallengeResult> ResolveChallengeAsync(byte[] challengePayload, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AuthChallengeResult
        {
            Success = _responsePayload.Length > 0,
            ResponsePayload = _responsePayload.ToArray(),
            Notes = $"Replay response from {_source}",
            HttpStatusCode = null
        });
    }
}
