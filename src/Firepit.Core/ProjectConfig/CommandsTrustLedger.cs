using System.IO;
using System.Security.Cryptography;
using System.Text;
using Firepit.Core.State;

namespace Firepit.Core.ProjectConfig;

/// <summary>
/// Trust ledger for per-project <c>commands[]</c> with shell entries. The
/// risk model: a cloned repo's <c>.firepit/config.json</c> can ship arbitrary
/// shell commands that the user might click unsuspectingly from the toolbar.
/// Issue #11 Phase A mitigation: prompt once per (project, file-hash); URL
/// and prompt-type commands skip the gate because they can't execute local
/// code on click.
/// </summary>
public sealed class CommandsTrustLedger
{
    private readonly IStateStore _stateStore;

    public CommandsTrustLedger(IStateStore stateStore)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    /// <summary>
    /// Returns the SHA-256 of <c>&lt;projectPath&gt;/.firepit/config.json</c>
    /// (hex, lowercase). Returns null if the file doesn't exist or can't be
    /// read — caller should treat that as "no shell commands present" since
    /// there's nothing to gate.
    /// </summary>
    public static string? HashConfigFile(string projectPath)
    {
        try
        {
            var path = Path.Combine(projectPath, ".firepit", "config.json");
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var bytes = SHA256.HashData(stream);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// True if the user has previously trusted <paramref name="projectPath"/>
    /// at this exact <paramref name="hash"/>. Any byte-level edit to the
    /// config file invalidates the previous trust (caller compares the
    /// current file hash to what was stored).
    /// </summary>
    public bool IsTrusted(string projectPath, string hash)
    {
        var state = _stateStore.Load();
        return state.TrustedCommands?.Any(t =>
            string.Equals(t.ProjectPath, projectPath, PlatformText.Paths) &&
            string.Equals(t.ConfigSha256, hash, StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    /// <summary>
    /// Record a trust grant. If an entry already exists for this project
    /// (with a different hash), it's replaced — there's only ever one
    /// trusted hash per project at a time.
    /// </summary>
    public void Trust(string projectPath, string hash)
    {
        var state = _stateStore.Load();
        var kept = (state.TrustedCommands ?? [])
            .Where(t => !string.Equals(t.ProjectPath, projectPath, PlatformText.Paths))
            .ToList();
        kept.Add(new TrustedProjectCommands(projectPath, hash));
        _stateStore.Save(state with { TrustedCommands = kept });
    }

    /// <summary>
    /// Forget any trust grant for <paramref name="projectPath"/>. Used when
    /// the user explicitly revokes via Settings (not yet exposed in UI).
    /// </summary>
    public void Revoke(string projectPath)
    {
        var state = _stateStore.Load();
        if (state.TrustedCommands is null) return;
        var kept = state.TrustedCommands
            .Where(t => !string.Equals(t.ProjectPath, projectPath, PlatformText.Paths))
            .ToList();
        if (kept.Count != state.TrustedCommands.Count)
        {
            _stateStore.Save(state with { TrustedCommands = kept });
        }
    }
}
