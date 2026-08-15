using Firepit.Core.Inbox;
using Firepit.Core.Sessions;

namespace Firepit.Core.Tests.Inbox;

public class InboxDeliveryPolicyTests
{
    private const string Project = @"D:\repos\demo";
    private static readonly string[] OneMessage = ["2026-01-01T00-00-00Z-from-a-hello.md"];

    /// <summary>Settle a project so the next Evaluate can deliver.</summary>
    private static InboxDeliveryPolicy Settled(int sweeps = 2)
    {
        var policy = new InboxDeliveryPolicy(sweeps);
        for (var i = 1; i < sweeps; i++)
        {
            policy.Evaluate(Project, SessionState.Embers, isUsersFocusedTab: false, OneMessage);
        }
        return policy;
    }

    [Theory]
    [InlineData(SessionState.Burning)]
    [InlineData(SessionState.Igniting)]
    [InlineData(SessionState.Cold)]
    [InlineData(SessionState.Dead)]
    public void NeverDeliversUnlessIdle(SessionState state)
    {
        var policy = new InboxDeliveryPolicy(1);

        var decision = policy.Evaluate(Project, state, isUsersFocusedTab: false, OneMessage);

        Assert.Equal(InboxDeliveryFlavour.None, decision.Flavour);
        Assert.Empty(decision.MessageIds);
    }

    [Fact]
    public void HoldsUntilIdleForTheRequiredNumberOfSweeps()
    {
        var policy = new InboxDeliveryPolicy(idleSweepsRequired: 3);

        Assert.Equal(InboxDeliveryFlavour.None,
            policy.Evaluate(Project, SessionState.Embers, false, OneMessage).Flavour);
        Assert.Equal(InboxDeliveryFlavour.None,
            policy.Evaluate(Project, SessionState.Embers, false, OneMessage).Flavour);
        Assert.Equal(InboxDeliveryFlavour.Act,
            policy.Evaluate(Project, SessionState.Embers, false, OneMessage).Flavour);
    }

    [Fact]
    public void ActivityResetsTheIdleStreak()
    {
        var policy = new InboxDeliveryPolicy(idleSweepsRequired: 2);

        policy.Evaluate(Project, SessionState.Embers, false, OneMessage);
        // One burst of output mid-way and the session has to settle again.
        policy.Evaluate(Project, SessionState.Burning, false, OneMessage);

        Assert.Equal(InboxDeliveryFlavour.None,
            policy.Evaluate(Project, SessionState.Embers, false, OneMessage).Flavour);
        Assert.Equal(InboxDeliveryFlavour.Act,
            policy.Evaluate(Project, SessionState.Embers, false, OneMessage).Flavour);
    }

    [Fact]
    public void BackgroundTabIsToldToAct()
    {
        var decision = Settled()
            .Evaluate(Project, SessionState.Embers, isUsersFocusedTab: false, OneMessage);

        Assert.Equal(InboxDeliveryFlavour.Act, decision.Flavour);
        Assert.Equal(OneMessage, decision.MessageIds);
    }

    [Fact]
    public void TheUsersOwnTabIsToldToPresentAndWait()
    {
        var decision = Settled()
            .Evaluate(Project, SessionState.Embers, isUsersFocusedTab: true, OneMessage);

        Assert.Equal(InboxDeliveryFlavour.PresentAndWait, decision.Flavour);
    }

    [Fact]
    public void DeliveredMessagesAreNotHandedOverAgain()
    {
        var policy = Settled();
        var first = policy.Evaluate(Project, SessionState.Embers, false, OneMessage);
        policy.MarkDelivered(Project, first.MessageIds);

        // Settle again — the file is still there because the agent hasn't
        // completed it, and that must not read as a fresh arrival.
        policy.Evaluate(Project, SessionState.Embers, false, OneMessage);
        var second = policy.Evaluate(Project, SessionState.Embers, false, OneMessage);

        Assert.Equal(InboxDeliveryFlavour.None, second.Flavour);
    }

    [Fact]
    public void OnlyTheUndeliveredIdsGoOut()
    {
        var policy = Settled();
        var first = policy.Evaluate(Project, SessionState.Embers, false, OneMessage);
        policy.MarkDelivered(Project, first.MessageIds);

        string[] both = [OneMessage[0], "2026-01-02T00-00-00Z-from-b-second.md"];
        policy.Evaluate(Project, SessionState.Embers, false, both);
        var second = policy.Evaluate(Project, SessionState.Embers, false, both);

        Assert.Equal(InboxDeliveryFlavour.Act, second.Flavour);
        Assert.Equal(["2026-01-02T00-00-00Z-from-b-second.md"], second.MessageIds);
    }

    [Fact]
    public void DeliveringCountsAsWorkStarting()
    {
        var policy = Settled();
        var first = policy.Evaluate(Project, SessionState.Embers, false, OneMessage);
        policy.MarkDelivered(Project, first.MessageIds);

        // A message landing on the very next sweep waits for a new idle streak
        // rather than piling onto work that just started.
        string[] next = ["2026-01-02T00-00-00Z-from-b-second.md"];
        Assert.Equal(InboxDeliveryFlavour.None,
            policy.Evaluate(Project, SessionState.Embers, false, next).Flavour);
        Assert.Equal(InboxDeliveryFlavour.Act,
            policy.Evaluate(Project, SessionState.Embers, false, next).Flavour);
    }

    [Fact]
    public void AnEmptyInboxIsNotADelivery()
    {
        var decision = Settled()
            .Evaluate(Project, SessionState.Embers, false, []);

        Assert.Equal(InboxDeliveryFlavour.None, decision.Flavour);
    }

    [Fact]
    public void ProjectsTrackTheirIdleStreaksSeparately()
    {
        var policy = new InboxDeliveryPolicy(idleSweepsRequired: 2);
        const string other = @"D:\repos\other";

        policy.Evaluate(Project, SessionState.Embers, false, OneMessage);
        // A busy sweep elsewhere must not settle or unsettle this one.
        policy.Evaluate(other, SessionState.Burning, false, OneMessage);

        Assert.Equal(InboxDeliveryFlavour.Act,
            policy.Evaluate(Project, SessionState.Embers, false, OneMessage).Flavour);
    }
}
