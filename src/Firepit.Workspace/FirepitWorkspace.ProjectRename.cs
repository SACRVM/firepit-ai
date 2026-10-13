using Firepit.Core;
using Firepit.Core.Projects;
using Firepit.Core.Settings;
using Firepit.Mcp;
using Serilog;

namespace Firepit.Workspace;

/// <summary>
/// firepit_rename_project — the atomic rename cascade (inbox request from
/// firepit-central). Only Firepit can do this without a restart, because only
/// Firepit can let go of its own folder handles first: the knowledge scope's
/// FileSystemWatcher on <c>.firepit/</c> lives for every project, and
/// Microsoft.Data.Sqlite pools connections on <c>knowledge.db</c>.
/// </summary>
/// <remarks>
/// A running agent in the folder is another matter. On Windows its working
/// directory pins the folder outright; on Linux the rename succeeds and the
/// agent keeps working in a directory that now has a different name, writing
/// its history under the old key. Either way a session in the project has to
/// end first — the shell refuses while its tab is open, and without a shell
/// the caller is told to close the Claude Code session there.
/// </remarks>
public sealed partial class FirepitWorkspace
{
    public Task<RenameProjectResult> RenameProjectAsync(
        string? from, string? fromPath, string to, bool renameFolder, bool migrateHistory) =>
        Serialized(() =>
        {
            try
            {
                to = to.Trim();
                if (to.Length == 0)
                    return new RenameProjectResult(false, "New name is empty.");
                if (to.IndexOfAny(InvalidNameChars) >= 0)
                    return new RenameProjectResult(false,
                        "New name contains characters not allowed in a folder name.");

                // Resolve the source — by path when given (the escape hatch
                // for blank or ambiguous names), else by name, where the
                // empty string is a legal, addressable name.
                Firepit.Core.Projects.Project? source;
                if (fromPath is not null)
                {
                    var full = Path.GetFullPath(fromPath);
                    source = _allProjects.FirstOrDefault(p => PathsEqual(p.Path, full));
                    if (source is null)
                        return new RenameProjectResult(false, $"No registered project at {fromPath}.");
                }
                else
                {
                    var matches = _allProjects
                        .Where(p => string.Equals(p.Name, from ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (matches.Count > 1)
                        return new RenameProjectResult(false,
                            $"Project name '{from}' is ambiguous ({matches.Count} matches) — pass fromPath.");
                    source = matches.FirstOrDefault();
                    if (source is null)
                        return new RenameProjectResult(false, $"Unknown project: '{from}'.");
                }

                var root = Path.GetFullPath(_settings.ProjectsRoot);
                if (PathsEqual(source.Path, Path.Combine(root, ".firepit")))
                    return new RenameProjectResult(false,
                        "The .firepit meta project cannot be renamed — Firepit pins it by name.");

                if (_tabs.SessionState(source.Path) is not null)
                    return new RenameProjectResult(false,
                        $"'{source.Name}' has an open tab. Close it first (firepit_close_tab) — " +
                        "the running agent process holds the folder as its working directory, " +
                        "which nothing can rename around.");

                if (_allProjects.Any(p => !ReferenceEquals(p, source) &&
                        string.Equals(p.Name, to, StringComparison.OrdinalIgnoreCase)))
                    return new RenameProjectResult(false,
                        $"A project named '{to}' already exists — pick another name.");

                var parent = Path.GetDirectoryName(Path.GetFullPath(source.Path));
                var underRoot = parent is not null && PathsEqual(parent, root);
                var warnings = new List<string>();

                var doRenameFolder = renameFolder && underRoot;
                if (renameFolder && !underRoot)
                {
                    warnings.Add(
                        $"{source.Path} is not directly under the projects root — folder left " +
                        "in place, only the registered name changes.");
                }

                var newPath = doRenameFolder ? Path.Combine(parent!, to) : Path.GetFullPath(source.Path);
                if (doRenameFolder && PathsEqual(newPath, source.Path))
                    doRenameFolder = false; // folder already carries the new name

                var manualIndex = (_settings.Projects ?? [])
                    .ToList()
                    .FindIndex(p => PathsEqual(p.Path, source.Path));
                if (!doRenameFolder && manualIndex < 0)
                    return new RenameProjectResult(false,
                        "Discovered projects take their name from the folder — a name-only " +
                        "rename needs a manual registry entry. Rename the folder instead " +
                        "(renameFolder=true).");

                if (doRenameFolder && Directory.Exists(newPath))
                    return new RenameProjectResult(false, $"Target folder already exists: {newPath}");

                var folderRenamed = false;
                var historyMigrated = false;
                var oldPath = Path.GetFullPath(source.Path);

                if (doRenameFolder)
                {
                    // Step 1 — let go of our own handles: drop the knowledge
                    // scope (disposes its .firepit watcher) and flush the
                    // sqlite pool (knowledge.db + WAL). ReloadProjectList at
                    // the end re-attaches everything under the new identity.
                    _allProjects.RemoveAll(p => PathsEqual(p.Path, oldPath));
                    SyncKnowledgeScopes();
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                    try
                    {
                        Directory.Move(oldPath, newPath);
                        folderRenamed = true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        ReloadProjectList();
                        return new RenameProjectResult(false,
                            $"Folder rename failed: {ex.Message}. Firepit released its own " +
                            "handles, so something else still holds the folder — an external " +
                            "shell, editor, or running process inside it.");
                    }

                    if (migrateHistory)
                    {
                        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        var oldHistory = ClaudeHistoryKey.GetHistoryDir(profile, oldPath);
                        var newHistory = ClaudeHistoryKey.GetHistoryDir(profile, newPath);
                        if (Directory.Exists(oldHistory))
                        {
                            if (Directory.Exists(newHistory))
                            {
                                warnings.Add(
                                    $"History dir for the new key already exists ({newHistory}) — " +
                                    "not merged; both left in place.");
                            }
                            else
                            {
                                try
                                {
                                    Directory.Move(oldHistory, newHistory);
                                    historyMigrated = true;
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                {
                                    // The one rollback that matters: a renamed
                                    // folder with orphaned history is exactly
                                    // the silent data loss this tool exists to
                                    // prevent.
                                    try
                                    {
                                        Directory.Move(newPath, oldPath);
                                        ReloadProjectList();
                                        return new RenameProjectResult(false,
                                            $"History migration failed ({ex.Message}) — folder " +
                                            "rename rolled back, nothing changed.");
                                    }
                                    catch (Exception rollbackEx)
                                    {
                                        Log.Error(rollbackEx, "rename_project: rollback failed");
                                        ReloadProjectList();
                                        return new RenameProjectResult(false,
                                            $"History migration failed ({ex.Message}) AND the folder " +
                                            $"rollback failed ({rollbackEx.Message}) — folder is at " +
                                            $"{newPath}, history still under the old key. Fix by hand: " +
                                            $"move {oldHistory} to {newHistory}.");
                                    }
                                }
                            }
                        }
                    }
                }

                // Metadata after the point of no return: failures degrade to
                // warnings — rolling back a successful folder+history rename
                // over a cosmetic id would be worse than reporting.
                try
                {
                    var config = SafeLoadProjectConfig(newPath);
                    if (config is not null && !string.Equals(config.Id, to, StringComparison.Ordinal))
                    {
                        _projectConfigStore.Save(newPath, config with { Id = to });
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "rename_project: could not update config.json id");
                    warnings.Add($"Could not update .firepit/config.json id: {ex.Message}");
                }

                if (manualIndex >= 0)
                {
                    var entries = (_settings.Projects ?? []).ToList();
                    entries[manualIndex] = entries[manualIndex] with { Name = to, Path = newPath };
                    _settings = _settings with { Projects = entries };
                    try
                    {
                        _settingsStore.Save(_settings);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "rename_project: could not persist settings.json");
                        warnings.Add($"Could not save settings.json: {ex.Message}");
                    }
                }

                ReloadProjectList();

                var project = _allProjects.FirstOrDefault(p => PathsEqual(p.Path, newPath));
                if (project is null)
                    return new RenameProjectResult(false,
                        $"Renamed to {newPath}, but the project did not reappear in the list — see the log.",
                        to, newPath, folderRenamed, historyMigrated,
                        warnings.Count > 0 ? warnings : null);

                Log.Information(
                    "MCP rename_project: '{Old}' → '{New}' (folder={Folder}, history={History})",
                    source.Name, project.Name, folderRenamed, historyMigrated);
                var message =
                    $"Renamed '{source.Name}' → '{project.Name}'." +
                    (folderRenamed ? $" Folder: {newPath}." : " Folder untouched.") +
                    (historyMigrated ? " Claude history + auto-memory migrated." : string.Empty);
                return new RenameProjectResult(
                    true, message, project.Name, project.Path, folderRenamed, historyMigrated,
                    warnings.Count > 0 ? warnings : null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "firepit_rename_project failed");
                return new RenameProjectResult(false, ex.Message);
            }
        });

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), PlatformText.Paths);
}
