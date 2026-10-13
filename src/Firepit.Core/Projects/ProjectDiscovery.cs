using System.IO;
using Firepit.Core.Agents;
using Firepit.Core.Platform;

namespace Firepit.Core.Projects;

public sealed class ProjectDiscovery : IProjectDiscovery
{
    private readonly IReadOnlyList<IAgentAdapter> _adapters;

    public ProjectDiscovery(IEnumerable<IAgentAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToArray();
        if (_adapters.Count == 0)
        {
            throw new ArgumentException("At least one agent adapter is required.", nameof(adapters));
        }
    }

    public IReadOnlyList<Project> Scan(string projectsRoot, IEnumerable<Project>? manualEntries = null)
    {
        ArgumentNullException.ThrowIfNull(projectsRoot);

        var manual = manualEntries?.ToArray() ?? [];
        var manualPaths = new HashSet<string>(
            manual.Select(p => Path.GetFullPath(p.Path)),
            PlatformText.PathComparer);

        var discovered = new List<Project>();
        if (Directory.Exists(projectsRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(projectsRoot))
            {
                var fullPath = Path.GetFullPath(directory);
                if (manualPaths.Contains(fullPath))
                {
                    continue;
                }

                var match = MatchAdapter(fullPath);
                if (match is null)
                {
                    continue;
                }

                discovered.Add(new Project(
                    Name: Path.GetFileName(directory),
                    Path: fullPath,
                    AdapterId: match.Id));
            }
        }

        // The .firepit meta-project always pins to the top — it's the hub for
        // cross-project work, so finding it should never require scrolling.
        // OrderBy is stable; ties preserve the manual-then-discovered ordering.
        return manual
            .Concat(discovered.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => string.Equals(
                p.Name,
                MetaProjectBootstrapper.MetaProjectName,
                StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();
    }

    private IAgentAdapter? MatchAdapter(string projectPath)
    {
        foreach (var adapter in _adapters)
        {
            foreach (var marker in adapter.ProjectMarkers)
            {
                var candidate = Path.Combine(projectPath, marker);
                if (Directory.Exists(candidate) || File.Exists(candidate))
                {
                    return adapter;
                }
            }
        }
        return null;
    }
}
