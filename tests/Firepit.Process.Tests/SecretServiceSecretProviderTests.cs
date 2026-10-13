using Firepit.Process;

namespace Firepit.Process.Tests;

public class SecretServiceSecretProviderTests
{
    [Fact]
    public void ACredToken_IsLookedUpByItsTarget()
    {
        var asked = new List<string>();
        var provider = new SecretServiceSecretProvider(target =>
        {
            asked.Add(target);
            return target == "firepit/fishbowl" ? "s3cret" : null;
        });

        Assert.True(provider.TryResolve("${cred:firepit/fishbowl}", out var value));
        Assert.Equal("s3cret", value);
        Assert.Equal(["firepit/fishbowl"], asked);
    }

    [Fact]
    public void AMissingSecret_LeavesTheTokenUnresolved()
    {
        var provider = new SecretServiceSecretProvider(_ => null);

        Assert.False(provider.TryResolve("${cred:firepit/nothing}", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void OtherTokens_AreNotItsBusiness()
    {
        var provider = new SecretServiceSecretProvider(_ => throw new InvalidOperationException("must not be asked"));

        Assert.False(provider.TryResolve("${env:HOME}", out _));
    }

    [UnixFact]
    public void TheRealLookup_AnswersNullForATargetThatWasNeverStored()
    {
        // Exercises secret-tool itself where it exists; without it, or without
        // a running Secret Service, the answer is the same null.
        var provider = new SecretServiceSecretProvider();

        Assert.False(provider.TryResolve($"${{cred:firepit/test-{Guid.NewGuid():N}}}", out _));
    }
}
