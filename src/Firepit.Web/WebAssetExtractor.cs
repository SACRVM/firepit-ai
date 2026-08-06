using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Firepit.Web;

internal static class WebAssetExtractor
{
    private static readonly Dictionary<string, string> ResourceMap = new()
    {
        ["Firepit.Web.Resources.terminal.html"]   = "terminal.html",
        ["Firepit.Web.Resources.xterm.js"]        = "xterm.js",
        ["Firepit.Web.Resources.xterm.css"]       = "xterm.css",
        ["Firepit.Web.Resources.addon-fit.js"]    = "addon-fit.js",
        ["Firepit.Web.Resources.addon-search.js"] = "addon-search.js",
        ["Firepit.Web.Resources.addon-web-links.js"] = "addon-web-links.js",
        ["Firepit.Web.Resources.CascadiaCode.ttf"] = "CascadiaCode.ttf",
    };

    // Cache key is a content hash of the embedded resources, NOT the assembly
    // version. Dev builds stay on 1.0.0.0 forever, so a version-keyed cache
    // would never invalidate when terminal.html changes — and the user would
    // have to nuke %LOCALAPPDATA%\Firepit\WebAssets by hand on every UI tweak.
    public static string Extract()
    {
        var assembly = typeof(WebAssetExtractor).Assembly;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var assetsRoot = Path.Combine(root, "Firepit", "WebAssets");
        var hash = ComputeContentHash(assembly);
        var directory = Path.Combine(assetsRoot, hash);
        var marker = Path.Combine(directory, ".extracted");

        if (!File.Exists(marker))
        {
            Directory.CreateDirectory(directory);
            foreach (var (resourceName, fileName) in ResourceMap)
            {
                using var source = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException(
                        $"Embedded resource '{resourceName}' not found in {assembly.GetName().Name}.");
                using var destination = File.Create(Path.Combine(directory, fileName));
                source.CopyTo(destination);
            }
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }

        TryPruneOrphans(assetsRoot, keep: hash);
        return directory;
    }

    private static string ComputeContentHash(Assembly assembly)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8192];
        foreach (var resourceName in ResourceMap.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{resourceName}' not found in {assembly.GetName().Name}.");
            hasher.AppendData(Encoding.UTF8.GetBytes(resourceName + "\0"));
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).AsSpan(0, 16).ToString().ToLowerInvariant();
    }

    private static void TryPruneOrphans(string assetsRoot, string keep)
    {
        try
        {
            if (!Directory.Exists(assetsRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(assetsRoot))
            {
                if (string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { Directory.Delete(dir, recursive: true); }
                catch { /* in use by another instance — best effort */ }
            }
        }
        catch { /* directory enumeration failed — best effort */ }
    }
}
