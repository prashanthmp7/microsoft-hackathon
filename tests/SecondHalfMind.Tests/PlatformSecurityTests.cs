using Microsoft.Extensions.Options;
using SecondHalfMind.Api.Security;
using SecondHalfMind.Data;

namespace SecondHalfMind.Tests;

/// <summary>Fan identity, operator key rotation and audit tamper evidence.</summary>
public class PlatformSecurityTests
{
    private static FanTokens Tokens(string key = "test-key") =>
        new(Options.Create(new SecurityOptions { FanTokenKey = key }));

    [Fact]
    public void Issued_fan_token_verifies_only_for_its_own_fan()
    {
        var tokens = Tokens();
        var (fanId, token) = tokens.Issue();
        Assert.True(tokens.Verify(token, fanId));
        Assert.False(tokens.Verify(token, Guid.NewGuid().ToString()));
        Assert.False(tokens.Verify($"{fanId}.forged", fanId));
        Assert.False(tokens.Verify(null, fanId));
    }

    [Fact]
    public void Fan_token_from_another_key_is_rejected()
    {
        var (fanId, token) = Tokens("other").Issue();
        Assert.False(Tokens().Verify(token, fanId));
    }

    [Fact]
    public void Operator_keys_support_rotation()
    {
        var options = new SecurityOptions { OperatorKey = "old", AdditionalOperatorKeys = ["new"] };
        var keys = options.OperatorKeys.ToList();
        Assert.True(OperatorKeyFilter.Matches("old", keys));
        Assert.True(OperatorKeyFilter.Matches("new", keys));
        Assert.False(OperatorKeyFilter.Matches("guess", keys));
        Assert.False(OperatorKeyFilter.Matches("", keys));
    }

    [Fact]
    public void Audit_chain_detects_tampering()
    {
        var log = new InMemoryAuditLog();
        for (var i = 0; i < 5; i++)
        {
            log.Record("stats", "agent.rejected", "m1", "blocked", $"entry {i}");
        }

        var chain = log.Recent(null, 100).Reverse().ToList();
        Assert.Equal(-1, InMemoryAuditLog.Verify(chain));

        chain[2] = chain[2] with { Detail = "edited" };
        Assert.Equal(2, InMemoryAuditLog.Verify(chain));

        var removed = log.Recent(null, 100).Reverse().Where((_, i) => i != 1).ToList();
        Assert.Equal(1, InMemoryAuditLog.Verify(removed));
    }
}
