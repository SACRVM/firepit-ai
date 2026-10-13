namespace Firepit.Core.ProjectConfig;

/// <summary>
/// Pure-function helpers for mutating a <see cref="ProjectConfig"/>'s commands
/// list. Kept in Firepit.Core so the MCP <c>firepit_add_command</c> handler
/// (and any other call-site that grows up later) shares one definition of
/// "upsert by name" — including the case-insensitive match.
/// </summary>
public static class ProjectCommandMutator
{
    /// <summary>
    /// Returns a new list with <paramref name="newCommand"/> appended, or
    /// replacing the existing entry with the same name in the same group
    /// (both case-insensitive; no group matches no group). Names are unique
    /// within a group, not across groups: "Build debug" and "Run debug" are
    /// two commands. The replacement keeps the original position; appended
    /// entries land at the end.
    /// </summary>
    public static IReadOnlyList<ProjectCommand> Upsert(
        IReadOnlyList<ProjectCommand>? existing,
        ProjectCommand newCommand)
    {
        ArgumentNullException.ThrowIfNull(newCommand);
        var list = new List<ProjectCommand>(existing ?? Array.Empty<ProjectCommand>());
        var idx = list.FindIndex(c => SameName(c.Name, newCommand.Name) && SameGroup(c.Group, newCommand.Group));
        if (idx >= 0) list[idx] = newCommand;
        else          list.Add(newCommand);
        return list;
    }

    /// <summary>
    /// Returns a new list without the command <paramref name="name"/> means,
    /// plus whether anything changed. <paramref name="name"/> is either the
    /// phrase a person reads ("Git status": group, space, name) or a bare
    /// name; a phrase match wins. A bare name that several groups share is
    /// ambiguous — check <see cref="Ambiguous"/> first; this removes the
    /// first match. Removing a name that doesn't exist is a no-op.
    /// </summary>
    public static (IReadOnlyList<ProjectCommand> commands, bool removed) RemoveByName(
        IReadOnlyList<ProjectCommand>? existing,
        string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (existing is null || existing.Count == 0)
            return (Array.Empty<ProjectCommand>(), false);

        var index = existing.ToList().FindIndex(c => SameName(PhraseOf(c), name));
        if (index < 0) index = existing.ToList().FindIndex(c => SameName(c.Name, name));
        if (index < 0) return (existing.ToList(), false);

        var list = existing.ToList();
        list.RemoveAt(index);
        return (list, true);
    }

    /// <summary>
    /// The phrases a bare <paramref name="name"/> could mean when more than
    /// one group has a command of that name ("Build debug", "Run debug");
    /// empty when the name is a phrase, unique, or unknown.
    /// </summary>
    public static IReadOnlyList<string> Ambiguous(IReadOnlyList<ProjectCommand>? existing, string name)
    {
        if (existing is null || existing.Any(c => SameName(PhraseOf(c), name))) return [];
        var matches = existing.Where(c => SameName(c.Name, name)).ToList();
        return matches.Select(c => c.Group ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            ? matches.Select(PhraseOf).ToList()
            : [];
    }

    /// <summary>"Git status": the group and the name, as the bar and the panel show them.</summary>
    public static string PhraseOf(ProjectCommand command) =>
        string.IsNullOrWhiteSpace(command.Group) ? command.Name : $"{command.Group} {command.Name}";

    private static bool SameName(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameGroup(string? a, string? b) =>
        string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
}
