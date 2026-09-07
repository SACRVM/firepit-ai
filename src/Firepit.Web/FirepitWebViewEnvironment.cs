using System.IO;
using System.Runtime.Versioning;
using Microsoft.Web.WebView2.Core;

namespace Firepit.Web;

[SupportedOSPlatform("windows10.0.17763.0")]
public static class FirepitWebViewEnvironment
{
    private static readonly object Gate = new();
    private static Task<CoreWebView2Environment>? _initialization;

    public static Task<CoreWebView2Environment> GetAsync()
    {
        lock (Gate)
        {
            return _initialization ??= CreateAsync();
        }
    }

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        // Per instance, like everything else: two Firepits sharing one browser
        // profile fight over its lock files, and the loser starts without a
        // terminal at all.
        var userDataFolder = Path.Combine(Firepit.Core.FirepitPaths.Local, "WebView2");
        Directory.CreateDirectory(userDataFolder);
        return await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
    }
}
