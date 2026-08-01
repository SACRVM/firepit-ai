namespace Firepit.Core.Artifacts;

/// <summary>
/// One entry in a project's artifact pane — a *link* to a file the agent (or
/// the user) wants within reach, never a copy. Removing an entry removes the
/// link only; the file on disk is untouched.
///
/// Deliberately NOT a file browser: the list is curated, flat, and only ever
/// grows by an explicit act (an MCP call or a drop). Nothing enumerates
/// directories, so this stays an artifact shelf rather than an Explorer clone.
/// </summary>
/// <param name="Path">
/// Absolute path, or relative to the project root. Stored exactly as supplied
/// so a repo-relative link keeps working on another machine.
/// </param>
/// <param name="Label">Display name; falls back to the file name.</param>
/// <param name="Note">One line of context ("crash repro, step 4").</param>
/// <param name="AddedAtUtc">ISO-8601 UTC timestamp, used for ordering.</param>
public sealed record ArtifactEntry(
    string Path,
    string? Label = null,
    string? Note = null,
    string? AddedAtUtc = null);

/// <summary>
/// File-shape classification, derived from the extension at display time
/// rather than stored — a link whose target changes type must not keep a
/// stale label. Drives the icon and what a click does.
/// </summary>
public enum ArtifactKind
{
    /// <summary>png/jpg/gif/svg/webp/bmp — previewable.</summary>
    Image,

    /// <summary>md/markdown — previewable.</summary>
    Markdown,

    /// <summary>txt/log/json/yaml/csv and friends — previewable as plain text.</summary>
    Text,

    /// <summary>pdf/docx/xlsx — opens in the system handler.</summary>
    Document,

    /// <summary>
    /// exe/bat/cmd/ps1/msi — a click *runs code*. Carries its own icon so the
    /// list never makes an executable look like a document.
    /// </summary>
    Executable,

    /// <summary>zip/7z/rar/tar/gz.</summary>
    Archive,

    /// <summary>Anything else, including folders.</summary>
    Other,
}

/// <summary>
/// An <see cref="ArtifactEntry"/> resolved against a project: absolute path,
/// existence, and the classification the UI renders from. Mirrors the
/// ResolvedQuickLink shape — the raw entry stays a pure data record and all
/// filesystem knowledge lives in the resolve step.
/// </summary>
public sealed record ResolvedArtifact(
    string Path,
    string AbsolutePath,
    string Label,
    string? Note,
    string? AddedAtUtc,
    ArtifactKind Kind,
    bool Exists);
