using ZwiftClickV2.Bridge.Protocol;

namespace ZwiftClickV2.Bridge.Tests;

public class AuthFlowCoordinatorTests
{
    [Fact]
    public async Task TryResolveAsync_IgnoresNonChallengeMessages()
    {
        var coordinator = new AuthFlowCoordinator();
        var message = new ParsedZapMessage
        {
            Channel = ZapChannel.CH02_AsyncNotify,
            WasEncrypted = true,
            ApplicationOpcode = 0x38,
            Plaintext = new byte[] { 0x38, 0x01 }
        };

        var decision = await coordinator.TryResolveAsync(message);

        Assert.False(decision.IsChallengeDetected);
        Assert.False(decision.ShouldSendResponse);
    }

    [Fact]
    public async Task TryResolveAsync_DetectsChallengeButNoClientResponse()
    {
        var coordinator = new AuthFlowCoordinator();
        var message = new ParsedZapMessage
        {
            Channel = ZapChannel.CH04_SyncTx,
            WasEncrypted = true,
            ApplicationOpcode = 0x13,
            Plaintext = new byte[] { 0x13, 0xAA, 0xBB }
        };

        var decision = await coordinator.TryResolveAsync(message);

        Assert.True(decision.IsChallengeDetected);
        Assert.False(decision.ShouldSendResponse);
    }

    [Fact]
    public async Task TryResolveAsync_ReturnsResponseWhenClientSucceeds()
    {
        var client = new FakeAuthClient
        {
            Result = new AuthChallengeResult
            {
                Success = true,
                ResponsePayload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
                Notes = "Replay mock"
            }
        };

        var coordinator = new AuthFlowCoordinator(client);
        var message = new ParsedZapMessage
        {
            Channel = ZapChannel.CH04_SyncTx,
            WasEncrypted = true,
            ApplicationOpcode = 0x13,
            Plaintext = new byte[] { 0x13, 0x01 }
        };

        var decision = await coordinator.TryResolveAsync(message);

        Assert.True(decision.IsChallengeDetected);
        Assert.True(decision.ShouldSendResponse);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, decision.ResponsePayload);
    }

    [Fact]
    public async Task TryResolveAsync_DetectsChallengeBy5802Pattern()
    {
        var coordinator = new AuthFlowCoordinator();
        var message = new ParsedZapMessage
        {
            Channel = ZapChannel.CH04_SyncTx,
            WasEncrypted = true,
            ApplicationOpcode = 0x00,
            Plaintext = new byte[] { 0x58, 0x02, 0x00 }
        };

        var decision = await coordinator.TryResolveAsync(message);

        Assert.True(decision.IsChallengeDetected);
    }

    [Fact]
    public async Task ReplayAuthChallengeClient_ReturnsConfiguredPayload()
    {
        var client = new ReplayAuthChallengeClient(new byte[] { 0xFA, 0xCE }, "test");

        var result = await client.ResolveChallengeAsync(new byte[] { 0x13, 0x01 });

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 0xFA, 0xCE }, result.ResponsePayload);
        Assert.Contains("Replay", result.Notes);
    }

    private sealed class FakeAuthClient : IAuthChallengeClient
    {
        public AuthChallengeResult Result { get; set; } = new();

        public Task<AuthChallengeResult> ResolveChallengeAsync(byte[] challengePayload, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }
}
