using System.Net;
using System.Text;
using Markdig;

namespace Firepit.Core.Artifacts;

/// <summary>
/// Turns an artifact into a self-contained HTML document for the viewer.
/// </summary>
/// <remarks>
/// <para>
/// Pure: it takes a kind, a name and (for the readable kinds) the text already
/// loaded from disk, and returns a string. No file access and no WebView2, so
/// the interesting part — what the viewer will actually show — is testable
/// without a browser or a window.
/// </para>
/// <para>
/// Not every artifact belongs here. A PDF, an installer, an archive have real
/// handlers on the machine already, and a half-built viewer would be worse
/// than the one the user chose. <see cref="CanPreview"/> names the three kinds
/// Firepit renders itself; everything else still goes to the shell.
/// </para>
/// </remarks>
public static class ArtifactPreview
{
    /// <summary>
    /// Virtual host the viewer maps the artifact's own directory to. WebView2
    /// refuses <c>file://</c>, which is the rule the terminal already lives by,
    /// so an image — and any relative link inside a markdown file — is reached
    /// through this instead.
    /// </summary>
    public const string VirtualHost = "artifact.local";

    /// <summary>
    /// Text longer than this is truncated. A pinned build log can be hundreds
    /// of megabytes; pushing that through a WebView2 as one string hangs the
    /// UI thread on the way in and the renderer on the way out. Showing the
    /// beginning and saying so is the honest failure.
    /// </summary>
    public const int MaxTextChars = 2_000_000;

    /// <summary>The kinds Firepit renders itself.</summary>
    public static bool CanPreview(ArtifactKind kind) => kind switch
    {
        ArtifactKind.Markdown => true,
        ArtifactKind.Text     => true,
        ArtifactKind.Image    => true,
        _                     => false,
    };

    /// <summary>
    /// Build the document. <paramref name="text"/> is the file's content for
    /// <see cref="ArtifactKind.Markdown"/> and <see cref="ArtifactKind.Text"/>,
    /// and is ignored for an image — that one is fetched by the browser from
    /// <see cref="VirtualHost"/>, so nothing has to be read into memory.
    /// </summary>
    public static string Build(ArtifactKind kind, string fileName, string? text)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var body = kind switch
        {
            ArtifactKind.Markdown => RenderMarkdown(text ?? string.Empty),
            ArtifactKind.Text     => RenderText(text ?? string.Empty),
            ArtifactKind.Image    => RenderImage(fileName),
            _ => $"<p class=\"note\">Firepit has no viewer for {WebUtility.HtmlEncode(fileName)}.</p>",
        };

        return $"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <base href="https://{VirtualHost}/">
            <title>{WebUtility.HtmlEncode(fileName)}</title>
            <style>{Css}</style>
            </head>
            <body class="{(kind == ArtifactKind.Image ? "image" : "doc")}">
            {body}
            </body>
            </html>
            """;
    }

    private static string RenderMarkdown(string markdown)
    {
        // Advanced extensions carry the things a README actually uses — tables,
        // task lists, fenced code with a language, autolinks. Raw HTML inside
        // the markdown is left alone rather than stripped: it is the user's own
        // file, and <details> or an <img> tag is normal in one. The viewer
        // turns scripting off instead, so leaving it in costs nothing.
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
        return Markdown.ToHtml(Truncate(markdown, out var cut), pipeline)
            + (cut ? TruncationNote : string.Empty);
    }

    private static string RenderText(string text) =>
        $"<pre class=\"text\">{WebUtility.HtmlEncode(Truncate(text, out var cut))}</pre>"
        + (cut ? TruncationNote : string.Empty);

    private static string RenderImage(string fileName) =>
        $"<img src=\"{WebUtility.HtmlEncode(Uri.EscapeDataString(fileName))}\" alt=\"{WebUtility.HtmlEncode(fileName)}\">";

    private static string Truncate(string value, out bool truncated)
    {
        truncated = value.Length > MaxTextChars;
        return truncated ? value[..MaxTextChars] : value;
    }

    private static string TruncationNote =>
        $"<p class=\"note\">Truncated at {MaxTextChars:N0} characters — open the file itself for the rest.</p>";

    /// <summary>
    /// Firepit's own palette rather than a generic markdown stylesheet: the
    /// viewer opens next to the terminal and a white page beside it reads as a
    /// different program.
    /// </summary>
    private const string Css = """
        :root { color-scheme: dark; }
        body {
          margin: 0;
          padding: 28px 34px 60px;
          background: #15110D;
          color: #E8E2D8;
          font-family: "Segoe UI", system-ui, sans-serif;
          font-size: 14.5px;
          line-height: 1.65;
        }
        body.image {
          display: flex; align-items: center; justify-content: center;
          padding: 16px; min-height: 100vh; box-sizing: border-box;
        }
        /* Centred, not just capped. The cap is for line length — past about
           60rem prose stops being readable — but left-aligning it leaves a
           dead strip down the right of a maximised window that reads as a
           layout fault rather than a decision. */
        body.doc { max-width: 60rem; margin-inline: auto; }
        img { max-width: 100%; height: auto; }
        h1, h2, h3, h4 { color: #F5C97B; line-height: 1.25; margin: 1.6em 0 .5em; font-weight: 600; }
        h1 { font-size: 1.7em; border-bottom: 1px solid #332B22; padding-bottom: .3em; }
        h2 { font-size: 1.35em; border-bottom: 1px solid #2A2219; padding-bottom: .25em; }
        h1:first-child, h2:first-child { margin-top: 0; }
        a { color: #C8A96A; }
        a:hover { color: #F5C97B; }
        code, kbd, pre, .text {
          font-family: "Cascadia Code", Consolas, "Courier New", monospace;
          font-size: .9em;
        }
        code { background: #1F1A14; padding: .15em .38em; border-radius: 3px; }
        pre {
          background: #1A1612; border: 1px solid #2A2219; border-radius: 5px;
          padding: 12px 14px; overflow-x: auto;
        }
        pre code { background: none; padding: 0; }
        pre.text { white-space: pre-wrap; word-break: break-word; }
        blockquote {
          margin: 1em 0; padding: .1em 1em; color: #B8AE9E;
          border-left: 3px solid #4A3D2C; background: #191410;
        }
        table { border-collapse: collapse; margin: 1em 0; display: block; overflow-x: auto; }
        th, td { border: 1px solid #332B22; padding: 6px 11px; text-align: left; }
        th { background: #1F1A14; color: #F5C97B; }
        hr { border: 0; border-top: 1px solid #332B22; margin: 2em 0; }
        ul, ol { padding-left: 1.5em; }
        li { margin: .25em 0; }
        input[type="checkbox"] { accent-color: #C8A96A; }
        .note {
          margin-top: 2em; padding: 10px 14px; border-radius: 5px;
          background: #1F1A14; border: 1px solid #332B22; color: #8C7A5C;
          font-family: "Segoe UI", system-ui, sans-serif; font-size: .88em;
        }

        /* Chromium's default scrollbar is the one thing in this window that
           announces it is a browser. Same slim pill the rest of Firepit uses:
           12px like the WPF ScrollBar style, thumb inset by a 3px border in
           the track colour — the trick terminal.html documents, because
           transparent borders plus background-clip misbehave on WebKit
           pseudo-scrollbars. */
        ::-webkit-scrollbar { width: 12px; height: 12px; }
        ::-webkit-scrollbar-track { background-color: #15110D; }
        ::-webkit-scrollbar-thumb {
          background-color: #5C4D3E;
          border: 3px solid #15110D;
          border-radius: 6px;
          min-height: 36px;
        }
        ::-webkit-scrollbar-thumb:hover  { background-color: #7A6855; }
        ::-webkit-scrollbar-thumb:active { background-color: #A89F92; }
        ::-webkit-scrollbar-corner { background: transparent; }

        /* A code block sits on its own darker ground, so its bar has to as
           well — otherwise the inset border shows as a lighter frame against
           the block. */
        pre::-webkit-scrollbar-track { background-color: #1A1612; }
        pre::-webkit-scrollbar-thumb { border-color: #1A1612; }
        table::-webkit-scrollbar-track { background-color: #15110D; }
        """;
}
