using ZwiftClickV2.Bridge.Logging;

namespace ZwiftClickV2.Bridge.Protocol;

public sealed class AuthChallengeResult
{
    public bool Success { get; init; }
    public byte[] ResponsePayload { get; init; } = Array.Empty<byte>();
    public string Notes { get; init; } = string.Empty;
    public int? HttpStatusCode { get; init; }
}

public interface IAuthChallengeClient
{
    Task<AuthChallengeResult> ResolveChallengeAsync(byte[] challengePayload, CancellationToken cancellationToken = default);
}

public sealed class NullAuthChallengeClient : IAuthChallengeClient
{
    public Task<AuthChallengeResult> ResolveChallengeAsync(byte[] challengePayload, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AuthChallengeResult
        {
            Success = false,
            ResponsePayload = Array.Empty<byte>(),
            Notes = "No auth client configured"
        });
    }
}

public sealed class AuthFlowDecision
{
    public bool IsChallengeDetected { get; init; }
    public bool ShouldSendResponse { get; init; }
    public byte[] ResponsePayload { get; init; } = Array.Empty<byte>();
    public string Notes { get; init; } = string.Empty;
}

/// <summary>
/// Coordinador de flujo auth challenge/respuesta (stub incremental).
/// Deteccion heuristica actual: mensajes entrantes cifrados por CH04 con opcode 0x13,
/// o payload con marcador de rechazo 58 02 al inicio del plaintext.
/// </summary>
public sealed class AuthFlowCoordinator
{
    private readonly IAuthChallengeClient _authClient;
    private readonly StructuredLogger? _logger;

    public AuthFlowCoordinator(IAuthChallengeClient? authClient = null, StructuredLogger? logger = null)
    {
        _authClient = authClient ?? new NullAuthChallengeClient();
        _logger = logger;
    }

    public async Task<AuthFlowDecision> TryResolveAsync(ParsedZapMessage message, CancellationToken cancellationToken = default)
    {
        if (!LooksLikeAuthChallenge(message))
        {
            return new AuthFlowDecision
            {
                IsChallengeDetected = false,
                ShouldSendResponse = false,
                Notes = "Message is not an auth challenge candidate"
            };
        }

        _logger?.Log("auth_challenge_detected", "rx", message.Channel.ToString(), message.Plaintext,
            $"opcode=0x{message.ApplicationOpcode:X2} encrypted={message.WasEncrypted}");

        var result = await _authClient.ResolveChallengeAsync(message.Plaintext, cancellationToken);
        if (!result.Success || result.ResponsePayload.Length == 0)
        {
            _logger?.LogInfo($"Auth challenge unresolved: {result.Notes}");
            return new AuthFlowDecision
            {
                IsChallengeDetected = true,
                ShouldSendResponse = false,
                Notes = string.IsNullOrWhiteSpace(result.Notes)
                    ? "Auth client did not return a response payload"
                    : result.Notes
            };
        }

        _logger?.Log("auth_response_ready", "tx", "CH03", result.ResponsePayload,
            $"status={result.HttpStatusCode?.ToString() ?? "N/A"} notes={result.Notes}");

        return new AuthFlowDecision
        {
            IsChallengeDetected = true,
            ShouldSendResponse = true,
            ResponsePayload = result.ResponsePayload,
            Notes = result.Notes
        };
    }

    public static bool LooksLikeAuthChallenge(ParsedZapMessage message)
    {
        if (!message.WasEncrypted || message.Channel != ZapChannel.CH04_SyncTx || message.Plaintext.Length == 0)
            return false;

        if (message.ApplicationOpcode == 0x13)
            return true;

        if (message.Plaintext.Length >= 2 && message.Plaintext[0] == 0x58 && message.Plaintext[1] == 0x02)
            return true;

        return false;
    }
}
