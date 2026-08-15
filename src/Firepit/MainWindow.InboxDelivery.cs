using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Firepit.Core.Inbox;
using Firepit.Core.Settings;
using Firepit.Views;
using Serilog;

namespace Firepit;

/// <summary>
/// Cross-project auto-delivery: an inbox message reaches the target project's
/// agent on its own, instead of waiting for someone to press the Inbox button.
///
/// <para><b>Directional gate.</b> Which prompt gets delivered depends on
/// whether the target is the tab the user is sitting in. A background worker
/// is told to act; the user's own focused tab is told to present and wait for
/// a go. That makes the user's active tab the single human gate, and it is
/// also the circuit breaker: every chain that routes back to her stops there
/// on its own, so no separate loop guard is needed as long as she starts the
/// chain.</para>
///
/// <para><b>Idle only.</b> Delivery happens solely into a session sitting at
/// <see cref="SessionState.Embers"/>, held for a couple of sweeps. Embers is a
/// trustworthy "not working" signal here because the activity detector pins
/// Burning while the agent reports progress over OSC 9;4 — thinking and tool
/// calls produce no output but do not read as idle. No output is parsed for
/// this; the host stays transparent.</para>
///
/// <para><b>The inbox stays the transport.</b> Nothing is consumed here. If no
/// tab is open, or it is busy, or the session is dead, the message simply
/// stays in the folder and the badge keeps pointing at it — delivery is an
/// accelerator on top of the durable queue, never a replacement for it.</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>How often to look for deliverable messages.</summary>
    private static readonly TimeSpan InboxSweepInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Consecutive idle sweeps required before handing anything over. One is
    /// enough in principle; requiring two costs a couple of seconds and avoids
    /// delivering into a momentary lull between two tool calls that happened
    /// not to raise progress.
    /// </summary>
    private const int IdleSweepsBeforeDelivery = 2;

    private DispatcherTimer? _inboxDeliveryTimer;

    /// <summary>
    /// The decision itself — idle streaks and already-delivered ids. Not
    /// persisted on purpose: a message still sitting in the inbox after a
    /// restart is still outstanding, and re-offering it is the safer failure.
    /// </summary>
    private readonly InboxDeliveryPolicy _deliveryPolicy = new(IdleSweepsBeforeDelivery);

    private void StartInboxAutoDelivery()
    {
        if (_inboxDeliveryTimer is not null) return;
        if (!(_settings.Platform ?? PlatformSettings.Defaults).InboxAutoDeliverEnabled)
        {
            Log.Information("Inbox auto-delivery is off (platform.inboxAutoDeliverEnabled)");
            return;
        }

        _inboxDeliveryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = InboxSweepInterval,
        };
        _inboxDeliveryTimer.Tick += (_, _) => SweepInboxForDelivery();
        _inboxDeliveryTimer.Start();
        Log.Information("Inbox auto-delivery armed (sweep every {Seconds}s)", InboxSweepInterval.TotalSeconds);
    }

    private void StopInboxAutoDelivery()
    {
        if (_inboxDeliveryTimer is null) return;
        try { _inboxDeliveryTimer.Stop(); } catch { /* ignored */ }
        _inboxDeliveryTimer = null;
    }

    private void SweepInboxForDelivery()
    {
        // Snapshot: delivering can close or restart a tab, which mutates the map.
        foreach (var (projectPath, entry) in _openTabs.ToArray())
        {
            try
            {
                TryDeliverFor(projectPath, entry.Session);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Inbox auto-delivery sweep failed for {Path}", projectPath);
            }
        }
    }

    private void TryDeliverFor(string projectPath, SessionTab session)
    {
        // The gate: is this the tab the user is actually looking at? Window
        // activation counts — a selected tab in a background window is not
        // where anyone is sitting.
        var isUsersTab = IsActive
                      && Tabs.SelectedItem is System.Windows.Controls.TabItem selected
                      && ReferenceEquals(selected, _openTabs.GetValueOrDefault(projectPath).TabItem);

        var decision = _deliveryPolicy.Evaluate(
            projectPath, session.State, isUsersTab, PendingIds(projectPath));
        if (decision.Flavour == InboxDeliveryFlavour.None) return;

        var prompt = decision.Flavour == InboxDeliveryFlavour.PresentAndWait
            ? BuildPresentPrompt(decision.MessageIds.Count)
            : BuildActPrompt(decision.MessageIds.Count);

        if (!session.TryDeliverPrompt(prompt))
        {
            // Session went away between the decision and the write. Ids stay
            // unburned so the next sweep tries again.
            return;
        }

        _deliveryPolicy.MarkDelivered(projectPath, decision.MessageIds);
        Log.Information(
            "Inbox auto-delivery: handed {Count} message(s) to {Project} ({Flavour})",
            decision.MessageIds.Count,
            System.IO.Path.GetFileName(projectPath.TrimEnd(System.IO.Path.DirectorySeparatorChar)),
            decision.Flavour);
    }

    /// <summary>
    /// Ids currently sitting in the inbox, keyed by filename — which is what
    /// <c>firepit_inbox_complete</c> takes. Filtering out what was already
    /// delivered is the policy's job, not this one's.
    /// </summary>
    private static List<string> PendingIds(string projectPath)
    {
        var dir = System.IO.Path.Combine(projectPath, ".firepit", "inbox");
        if (!Directory.Exists(dir)) return [];
        try
        {
            return Directory
                .EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                .Select(System.IO.Path.GetFileName)
                .OfType<string>()
                .ToList();
        }
        catch (IOException)
        {
            // Transient — next sweep picks it up.
            return [];
        }
    }

    /// <summary>
    /// Background worker: it executes the dispatched task and reports back. No
    /// onward fan-out — a worker sending to another worker is the one place a
    /// guard would eventually be wanted, so the first cut simply doesn't.
    /// </summary>
    private static string BuildActPrompt(int count) =>
        $"Firepit inbox: {Plural(count)} arrived. Read {(count == 1 ? "it" : "them")} with the "
        + "firepit_inbox_list MCP tool, act on it, then mark it done with firepit_inbox_complete "
        + "(id = the entry's id). Stop and ask first before anything irreversible — deleting or "
        + "overwriting files, force-pushing, cutting a release, or sending anything outside this "
        + "machine. Do the dispatched work and report back; don't send this onward to other projects.";

    /// <summary>
    /// The user's own tab: her attention is the gate, so nothing runs until she
    /// says so. Firepit builds no confirmation dialog for this — the agent does
    /// the asking, in the terminal, where she already is.
    /// </summary>
    private static string BuildPresentPrompt(int count) =>
        $"Firepit inbox: {Plural(count)} arrived. Read {(count == 1 ? "it" : "them")} with the "
        + "firepit_inbox_list MCP tool and summarise what each one is asking for — then stop and "
        + "wait for my go before acting on anything.";

    private static string Plural(int count) =>
        count == 1 ? "1 new message" : $"{count} new messages";
}
