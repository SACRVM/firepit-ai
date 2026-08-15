using Firepit.Core.Sessions;

namespace Firepit.Core.Inbox;

/// <summary>Which prompt a delivery should carry, if any.</summary>
public enum InboxDeliveryFlavour
{
    /// <summary>Hold: busy, not settled yet, nothing new, or no live session.</summary>
    None,

    /// <summary>Background worker — execute the dispatched task and report back.</summary>
    Act,

    /// <summary>The user's own focused tab — summarise and wait for her go.</summary>
    PresentAndWait,
}

public sealed record InboxDeliveryDecision(
    InboxDeliveryFlavour Flavour,
    IReadOnlyList<string> MessageIds)
{
    public static readonly InboxDeliveryDecision Hold =
        new(InboxDeliveryFlavour.None, []);
}

/// <summary>
/// Decides whether a project's pending inbox messages may be handed to its
/// running agent, and in which flavour. Pure decision state — no filesystem,
/// no UI, no clock — so the rules that matter can be tested directly.
///
/// Two invariants it exists to hold:
/// <list type="bullet">
///   <item><b>Never mid-turn.</b> Only a session sitting at
///   <see cref="SessionState.Embers"/> for
///   <see cref="InboxDeliveryPolicy(int)">several consecutive sweeps</see>
///   qualifies. Anything else resets the streak, so a session has to look
///   settled again rather than merely pause.</item>
///   <item><b>Never twice.</b> An id handed over is remembered, so a message
///   the agent leaves sitting in the folder is not re-delivered on every
///   sweep.</item>
/// </list>
///
/// <see cref="Evaluate"/> is deliberately separate from
/// <see cref="MarkDelivered"/>: writing to a session can fail after the
/// decision is made, and ids must only be burned once the prompt is actually
/// on the wire.
/// </summary>
public sealed class InboxDeliveryPolicy
{
    private readonly int _idleSweepsRequired;
    private readonly Dictionary<string, int> _idleStreak = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _delivered = new(StringComparer.OrdinalIgnoreCase);

    public InboxDeliveryPolicy(int idleSweepsRequired = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(idleSweepsRequired, 1);
        _idleSweepsRequired = idleSweepsRequired;
    }

    /// <summary>
    /// Advance one sweep for <paramref name="projectKey"/> and decide what may
    /// be delivered. Call once per sweep per project — it advances the idle
    /// streak as a side effect.
    /// </summary>
    public InboxDeliveryDecision Evaluate(
        string projectKey,
        SessionState state,
        bool isUsersFocusedTab,
        IEnumerable<string> pendingIds)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectKey);
        ArgumentNullException.ThrowIfNull(pendingIds);

        if (state != SessionState.Embers)
        {
            _idleStreak[projectKey] = 0;
            return InboxDeliveryDecision.Hold;
        }

        var streak = _idleStreak.GetValueOrDefault(projectKey) + 1;
        _idleStreak[projectKey] = streak;
        if (streak < _idleSweepsRequired)
        {
            return InboxDeliveryDecision.Hold;
        }

        var fresh = pendingIds.Where(id => !_delivered.Contains(id)).ToList();
        if (fresh.Count == 0)
        {
            return InboxDeliveryDecision.Hold;
        }

        return new InboxDeliveryDecision(
            isUsersFocusedTab ? InboxDeliveryFlavour.PresentAndWait : InboxDeliveryFlavour.Act,
            fresh);
    }

    /// <summary>
    /// Burn the ids and treat the project as freshly busy — a delivery is work
    /// starting, so whatever lands on the next tick waits for a new idle streak.
    /// Call only after the prompt reached the session.
    /// </summary>
    public void MarkDelivered(string projectKey, IEnumerable<string> ids)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectKey);
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
        {
            _delivered.Add(id);
        }
        _idleStreak[projectKey] = 0;
    }

    /// <summary>Drop a closed project's streak so it doesn't linger.</summary>
    public void Forget(string projectKey) => _idleStreak.Remove(projectKey);
}
