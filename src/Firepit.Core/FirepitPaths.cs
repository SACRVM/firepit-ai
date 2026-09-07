using System.Text.RegularExpressions;

namespace Firepit.Core;

/// <summary>
/// Where this Firepit keeps its data, and who it is on the machine.
/// </summary>
/// <remarks>
/// <para>
/// Firepit is single-instance, which is right for the installed copy and was
/// also why a candidate build could not be looked at: a second Firepit does
/// not start, it focuses the first. Testing a change therefore meant killing
/// the window the work was happening in, so changes to the UI shipped
/// unexamined and the release was the first time anyone saw them.
/// </para>
/// <para>
/// A named instance is the way out. It answers to its own singleton pipe and
/// keeps its own settings, state, logs, browser profile and knowledge index,
/// so it can run beside the installed copy without either noticing the other.
/// The default instance is unnamed and its paths are byte-for-byte what they
/// have always been — an existing installation must not migrate anywhere.
/// </para>
/// <para>
/// Set once, from the entry point, before anything reads a path. A second
/// call with a different name throws rather than quietly splitting one
/// session's data across two roots, which is the kind of failure that only
/// shows up as "my settings are gone".
/// </para>
/// </remarks>
public static class FirepitPaths
{
    private const string ProductFolder = "Firepit";
    private const string BasePipeName = "firepit-singleton";
    private const string BaseMcpPipeName = "firepit-mcp";

    /// <summary>
    /// Conservative on purpose: this ends up in a directory name, so anything
    /// that could climb out of the product folder is refused rather than
    /// escaped.
    /// </summary>
    /// <remarks>
    /// Must begin with a letter or digit, which is what rules out <c>..</c> and
    /// <c>.hidden</c>. The <c>Firepit-</c> prefix means a bare <c>..</c> could
    /// not actually traverse anywhere — but a rule that is only safe because
    /// of a prefix somewhere else is one refactor away from not being safe, and
    /// the leading character also keeps a name from being read as another
    /// command-line switch. A trailing dot is refused separately: Windows
    /// silently strips those, so <c>x.</c> and <c>x</c> would be one folder
    /// under two names.
    /// </remarks>
    private static readonly Regex Allowed =
        new("^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$", RegexOptions.Compiled);

    private static bool _initialized;

    /// <summary>The instance name, or null for the default (installed) one.</summary>
    public static string? InstanceName { get; private set; }

    /// <summary>True when this process is the ordinary, installed Firepit.</summary>
    public static bool IsDefaultInstance => InstanceName is null;

    /// <summary>
    /// Whether <paramref name="name"/> may be used as an instance name. Pure,
    /// and public so the rule can be tested without a process that has already
    /// committed to an instance — the ambient state below chooses which name
    /// is in force, it does not decide what a legal one looks like.
    /// </summary>
    public static bool IsValidInstanceName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && Allowed.IsMatch(name.Trim())
        && !name.Trim().EndsWith('.');

    /// <summary>The data folder name for an instance. Pure; see
    /// <see cref="IsValidInstanceName"/> for why it is exposed.</summary>
    public static string FolderNameFor(string? instanceName) =>
        string.IsNullOrWhiteSpace(instanceName)
            ? ProductFolder
            : $"{ProductFolder}-{instanceName.Trim()}";

    /// <summary>The singleton pipe name for an instance. Pure.</summary>
    public static string SingletonPipeNameFor(string? instanceName) =>
        string.IsNullOrWhiteSpace(instanceName)
            ? BasePipeName
            : $"{BasePipeName}-{instanceName.Trim()}";

    /// <summary>
    /// Name this process's instance. <paramref name="name"/> null or empty
    /// selects the default. Call once, before any path is read.
    /// </summary>
    public static void Initialize(string? name)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        if (normalized is not null && !IsValidInstanceName(normalized))
        {
            throw new ArgumentException(
                $"Instance name '{normalized}' is not usable as a folder name. " +
                "Use letters, digits, dot, dash or underscore, up to 32 characters.",
                nameof(name));
        }

        if (_initialized)
        {
            if (!string.Equals(InstanceName, normalized, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"This process is already the '{InstanceName ?? "default"}' instance; " +
                    $"it cannot become '{normalized ?? "default"}' after paths have been used.");
            }
            return;
        }

        InstanceName = normalized;
        _initialized = true;
    }

    /// <summary>Roaming data root — settings live here.</summary>
    public static string Roaming => Root(Environment.SpecialFolder.ApplicationData);

    /// <summary>Local data root — state, logs, indexes, browser profile.</summary>
    public static string Local => Root(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// The singleton pipe this instance answers to. Named instances get their
    /// own, which is what lets them run at the same time as the installed copy.
    /// </summary>
    public static string SingletonPipeName => SingletonPipeNameFor(InstanceName);

    /// <summary>
    /// The MCP pipe this instance serves, and the one its agent sessions'
    /// bridges must dial.
    /// </summary>
    /// <remarks>
    /// Per instance for a sharper reason than the singleton pipe. Two Firepits
    /// listening on one pipe name both accept connections, and a bridge gets
    /// whichever Windows hands it — so an agent in the installed Firepit could
    /// find itself opening tabs and writing knowledge in the test instance,
    /// with nothing anywhere saying which one answered.
    /// </remarks>
    public static string McpPipeName => McpPipeNameFor(InstanceName);

    /// <summary>
    /// Environment variable naming the instance, exported into every agent
    /// session so its <c>firepit-mcp</c> bridge dials the Firepit it belongs to.
    /// </summary>
    public const string InstanceEnvironmentVariable = "FIREPIT_INSTANCE";

    /// <summary>The MCP pipe name for a given instance, seen from outside the
    /// process — used by the bridge, which is not this Firepit.</summary>
    public static string McpPipeNameFor(string? instanceName) =>
        string.IsNullOrWhiteSpace(instanceName)
            ? BaseMcpPipeName
            : $"{BaseMcpPipeName}-{instanceName.Trim()}";

    /// <summary>
    /// Suffix for anything the user reads — window title, log lines. Empty for
    /// the default instance: an installed Firepit should look like Firepit.
    /// A second window that does not say which one it is gets typed into by
    /// mistake, so this is not decoration.
    /// </summary>
    public static string DisplaySuffix => InstanceName is null ? string.Empty : $" [{InstanceName}]";

    /// <summary>The installed instance's roaming root, whatever this one is.</summary>
    public static string DefaultRoaming =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductFolder);

    private static string Root(Environment.SpecialFolder folder) =>
        Path.Combine(Environment.GetFolderPath(folder), FolderNameFor(InstanceName));
}
