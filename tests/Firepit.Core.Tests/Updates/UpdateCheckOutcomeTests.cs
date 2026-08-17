using Firepit.Core.Updates;

namespace Firepit.Core.Tests.Updates;

/// <summary>
/// The three answers an update check can give. The caption-bar badge can only
/// express one of them, which is why the About dialog needs a type that keeps
/// them apart.
/// </summary>
public class UpdateCheckOutcomeTests
{
    private static UpdateInfo Release(string version) =>
        new(Version.Parse(version), $"v{version}", "https://example.invalid", null, null, null, 0);

    [Fact]
    public void AnUpdateFound_IsNeitherUpToDateNorAFailure()
    {
        var outcome = new UpdateCheckOutcome(Release("1.2.0"), null, DateTimeOffset.UtcNow);

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.UpToDate);
    }

    [Fact]
    public void NoUpdateAfterASuccessfulCheck_IsUpToDate()
    {
        var outcome = new UpdateCheckOutcome(null, null, DateTimeOffset.UtcNow);

        Assert.True(outcome.UpToDate);
    }

    [Fact]
    public void AFailedCheck_IsNotUpToDate()
    {
        // The distinction the whole type exists for: "we asked and there is
        // nothing" versus "we could not ask". Collapsing them is what let an
        // installation sit on an old version believing it was current.
        var outcome = new UpdateCheckOutcome(null, "the network is unreachable", null);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.UpToDate);
    }

    [Fact]
    public void AFailedCheck_StillReportsWhenOneLastWorked()
    {
        // How long it has been failing is the part that tells someone whether
        // to care.
        var lastGood = DateTimeOffset.UtcNow.AddDays(-9);
        var outcome = new UpdateCheckOutcome(null, "403 rate limited", lastGood);

        Assert.False(outcome.Succeeded);
        Assert.Equal(lastGood, outcome.LastSuccessUtc);
    }
}
