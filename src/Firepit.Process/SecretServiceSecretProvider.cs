using System.ComponentModel;
using System.Text.RegularExpressions;
using Firepit.Core.Secrets;

namespace Firepit.Process;

/// <summary>
/// Resolves <c>${cred:&lt;target&gt;}</c> tokens from the freedesktop Secret
/// Service — KWallet on Plasma 6, GNOME Keyring elsewhere — the Linux
/// counterpart of <see cref="CredentialManagerSecretProvider"/>.
/// </summary>
/// <remarks>
/// Goes through <c>secret-tool</c> (libsecret's CLI) rather than D-Bus
/// bindings: one small process per lookup, at session start only, against a
/// dependency every desktop already has. A value is stored under the
/// attributes <c>service=firepit</c> and <c>target=&lt;target&gt;</c>:
/// <code>secret-tool store --label="Firepit: firepit/fishbowl" service firepit target firepit/fishbowl</code>
/// </remarks>
public sealed class SecretServiceSecretProvider : ISecretResolver
{
    private static readonly Regex CredTokenRegex = new(@"^\$\{cred:([^}]+)\}$", RegexOptions.Compiled);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, string?> _lookup;

    public SecretServiceSecretProvider()
        : this(LookUp)
    {
    }

    /// <param name="lookup">Answers a target with its secret, or null. For tests.</param>
    public SecretServiceSecretProvider(Func<string, string?> lookup)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
    }

    public bool TryResolve(string token, out string? value)
    {
        var match = CredTokenRegex.Match(token);
        value = match.Success ? _lookup(match.Groups[1].Value) : null;
        return value is not null;
    }

    private static string? LookUp(string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("secret-tool")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "lookup", "service", "firepit", "target", target })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(LookupTimeout))
            {
                // A locked wallet can sit on its unlock prompt; a session
                // start must not hang on it. The token stays unresolved and
                // the MCP health banner says so.
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            // secret-tool prints the stored value as-is; a value stored from
            // an interactive prompt ends without a newline, one piped in from
            // `echo` with one. Drop exactly that.
            return process.ExitCode == 0 ? stdout.Result.TrimEnd('\n') : null;
        }
        catch (Win32Exception)
        {
            // secret-tool is not installed.
            return null;
        }
    }
}
