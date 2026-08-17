using System.IO;
using System.Text.Json;
using Firepit.Core.ProjectConfig;

namespace Firepit.Core.Blueprints;

/// <summary>
/// Loads blueprints from <c>{metaProject}/blueprints/</c>. Blueprints live in
/// the <c>.firepit</c> meta project on purpose: they are user-editable data,
/// versioned with the meta project's own git repo, and the helper agent can
/// grow the catalogue without a Firepit release. <see cref="EnsureDefaults"/>
/// seeds the built-in "firepit" blueprint on first use; after that, disk is
/// the single source of truth.
/// </summary>
public sealed class BlueprintStore
{
    public const string BlueprintsDirName = "blueprints";
    public const string ManifestFileName = "blueprint.json";
    public const string FilesDirName = "files";

    /// <summary>
    /// Bump whenever a built-in CLAUDE.md section is added, so already-seeded
    /// meta projects pick it up once via <see cref="EnsureDefaults"/>.
    /// v2 (0.13.0) added the artifacts section.
    /// </summary>
    public const int CurrentManifestVersion = 3;

    private readonly string _blueprintsDir;

    public BlueprintStore(string projectsRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectsRoot);
        MetaProjectPath = Path.Combine(Path.GetFullPath(projectsRoot), ".firepit");
        _blueprintsDir = Path.Combine(MetaProjectPath, BlueprintsDirName);
    }

    public string MetaProjectPath { get; }

    public bool MetaProjectExists => Directory.Exists(MetaProjectPath);

    /// <summary>
    /// Seed the built-in "firepit" blueprint if the meta project exists and
    /// doesn't carry one yet. Never overwrites — once seeded, the on-disk
    /// copy is the user's to edit. Returns true when something was written.
    /// </summary>
    public bool EnsureDefaults()
    {
        if (!MetaProjectExists)
        {
            return false;
        }

        var dir = Path.Combine(_blueprintsDir, FirepitBlueprintDefaults.DefaultBlueprintName);
        if (File.Exists(Path.Combine(dir, ManifestFileName)))
        {
            return TopUpSeededSections(dir);
        }

        var manifest = new BlueprintManifest(
            Version: CurrentManifestVersion,
            Description: FirepitBlueprintDefaults.Description,
            EnsureProjectConfig: true,
            Gitignore: ProjectScaffolding.GitignoreEntries,
            ClaudeMd: BuiltInClaudeMdSections);

        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, ManifestFileName),
            JsonSerializer.Serialize(manifest, BlueprintJsonContext.Default.BlueprintManifest));

        SeedFile(dir, FirepitBlueprintDefaults.KnowledgeReadmePath, FirepitBlueprintDefaults.KnowledgeReadme);
        // Placeholder digest: apply copies it into projects that don't have
        // one yet, so the CLAUDE.md @import resolves immediately; Firepit's
        // knowledge service overwrites it with real pinned content.
        SeedFile(dir, FirepitBlueprintDefaults.PinnedDigestPath, FirepitBlueprintDefaults.PinnedDigestSeed);
        return true;
    }

    /// <summary>
    /// One-time migration of an already-seeded manifest to
    /// <see cref="CurrentManifestVersion"/>: appends built-in CLAUDE.md
    /// sections introduced by a newer Firepit, matched by marker so nothing
    /// the user wrote is touched. Without it a convention added in a later
    /// release only ever reaches fresh installs — every existing meta project
    /// stays frozen at the section set it was first seeded with.
    ///
    /// Gated on the manifest's version, not on "is a section missing": once
    /// the file is at the current version it is the user's alone, deletions
    /// included. Otherwise removing a section you don't want would just bring
    /// it back on the next launch.
    /// Returns true when the manifest was rewritten.
    /// </summary>
    private static bool TopUpSeededSections(string dir)
    {
        var manifestPath = Path.Combine(dir, ManifestFileName);
        BlueprintManifest? existing;
        try
        {
            existing = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath), BlueprintJsonContext.Default.BlueprintManifest);
        }
        catch (Exception)
        {
            // Hand-edited into invalid JSON — that's the user's file to fix.
            // Silently rewriting it would destroy the edit.
            return false;
        }
        if (existing is null || existing.Version >= CurrentManifestVersion)
        {
            return false;
        }

        var sections = existing.ClaudeMd?.ToList() ?? [];
        var known = sections.Select(s => s.Marker).ToHashSet(StringComparer.Ordinal);
        foreach (var builtIn in BuiltInClaudeMdSections)
        {
            if (known.Add(builtIn.Marker))
            {
                sections.Add(builtIn);
            }
        }

        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                existing with { Version = CurrentManifestVersion, ClaudeMd = sections },
                BlueprintJsonContext.Default.BlueprintManifest));
        return true;
    }

    private static IReadOnlyList<BlueprintManifestSection> BuiltInClaudeMdSections =>
    [
        // First, because it is the one an agent should read before the rest.
        new(FirepitBlueprintDefaults.FragmentsSectionMarker, FirepitBlueprintDefaults.FragmentsSection),
        new(FirepitBlueprintDefaults.InboxSectionMarker,     FirepitBlueprintDefaults.InboxSection),
        new(FirepitBlueprintDefaults.KnowledgeSectionMarker, FirepitBlueprintDefaults.KnowledgeSection),
        new(FirepitBlueprintDefaults.PinnedSectionMarker,    FirepitBlueprintDefaults.PinnedSection),
        new(FirepitBlueprintDefaults.ArtifactsSectionMarker, FirepitBlueprintDefaults.ArtifactsSection),
    ];

    private static void SeedFile(string blueprintDir, string relativePath, string content)
    {
        var target = Path.Combine(
            blueprintDir, FilesDirName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
    }

    public IReadOnlyList<Blueprint> LoadAll()
    {
        if (!Directory.Exists(_blueprintsDir))
        {
            return [];
        }

        var result = new List<Blueprint>();
        foreach (var dir in Directory.EnumerateDirectories(_blueprintsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var bp = TryLoadDir(dir);
            if (bp is not null)
            {
                result.Add(bp);
            }
        }

        return result;
    }

    public Blueprint? TryLoad(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '.']) >= 0)
        {
            // Blueprint names are bare folder names — anything path-like is
            // either a typo or a traversal attempt from MCP input.
            return null;
        }

        return TryLoadDir(Path.Combine(_blueprintsDir, name));
    }

    private static Blueprint? TryLoadDir(string dir)
    {
        var manifestPath = Path.Combine(dir, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        BlueprintManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath), BlueprintJsonContext.Default.BlueprintManifest);
        }
        catch (JsonException)
        {
            return null;
        }

        if (manifest is null)
        {
            return null;
        }

        var files = new List<BlueprintFile>();
        var filesRoot = Path.Combine(dir, FilesDirName);
        if (Directory.Exists(filesRoot))
        {
            foreach (var source in Directory.EnumerateFiles(filesRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(filesRoot, source).Replace('\\', '/');
                files.Add(new BlueprintFile(rel, source));
            }
        }

        return new Blueprint(
            Name: Path.GetFileName(dir.TrimEnd('\\', '/')),
            Description: manifest.Description ?? string.Empty,
            SourceDir: dir,
            EnsureProjectConfig: manifest.EnsureProjectConfig,
            GitignoreLines: manifest.Gitignore ?? [],
            ClaudeMdSections: manifest.ClaudeMd?
                .Select(s => new BlueprintClaudeMdSection(s.Marker, s.Content))
                .ToArray() ?? [],
            Files: files);
    }
}
