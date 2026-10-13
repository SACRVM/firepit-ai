using System.IO;

namespace Firepit.Core.ProjectConfig;

/// <summary>
/// Generates a commented JSONC scaffold for a project's <c>.firepit/config.json</c>.
/// All sections are present but empty/disabled so the file parses unchanged
/// and the user has a tour of every knob without surprises.
///
/// The reader (<see cref="ProjectConfigJsonContext"/>) is configured with
/// <c>ReadCommentHandling=Skip</c>, so the inline comments survive the
/// parse round-trip.
/// </summary>
public static class ProjectConfigScaffold
{
    /// <summary>
    /// Path to the per-project Firepit config file for the given project root.
    /// Does not check existence.
    /// </summary>
    public static string GetConfigPath(string projectPath) =>
        Path.Combine(projectPath, ".firepit", "config.json");

    /// <summary>
    /// Ensure <c>.firepit/config.json</c> exists for the given project. If
    /// missing, writes the scaffold (creating <c>.firepit/</c> as needed).
    /// Returns the absolute path either way. Never overwrites an existing file.
    /// </summary>
    public static string EnsureScaffold(string projectPath, string projectId)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(projectId);

        var target = GetConfigPath(projectPath);
        if (File.Exists(target)) return target;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, BuildScaffold(projectId));
        return target;
    }

    public static string BuildScaffold(string projectId) => $$"""
{
  // Firepit per-project config. JSONC: comments and trailing commas are allowed.
  // The firepit-commands mod for Claude Code reads it and picks up every save.

  "version": 1,
  "id": "{{projectId}}",

  // Project commands: buttons above the prompt, rows in the Firepit panel,
  // and /fire <group> <name>. Put each command in a group named after its
  // area (Run, Build, Test, Git, Open) and give it a short lowercase name,
  // unique within the group, so the two read as a phrase: "Git status".
  //   type         — "shell" (command + args, an argv, not a shell line)
  //                  | "claude-prompt" (prompt goes to the session as yours)
  //                  | "url" (opens in the browser)
  //   description  — one sentence for the panel
  //   bar          — false keeps it out of the bar; the panel lists it anyway
  //   cwd, env     — working dir (relative to the project), extra variables
  //   window       — "new" (default): a terminal window
  //                  "reuse:<id>": one at a time per id
  //                  "inline": runs in the background; the output comes back
  //                  in the chat, where the session can read it
  //   longRunning  — a background service (a systemd user unit) with stop
  //                  and logs in the panel; pair it with reuse:<id>
  //   keepOpenOnError — the window stays open on a non-zero exit
  //   confirm      — asks before it runs
  // Shell commands run only after you trust them in the panel, and again
  // after any of their command lines changes.
  // "commands": [
  //   { "group": "Run", "name": "dev", "description": "Starts the dev server.",
  //     "type": "shell", "command": "npm", "args": ["run", "dev"],
  //     "window": "reuse:dev", "longRunning": true },
  //   { "group": "Test", "name": "all", "description": "Runs the tests; the result lands in the chat.",
  //     "type": "shell", "command": "dotnet", "args": ["test"], "window": "inline" },
  //   { "group": "Git", "name": "status", "type": "shell",
  //     "command": "git", "args": ["status", "-sb"], "window": "inline" },
  //   { "group": "Claude", "name": "review", "type": "claude-prompt",
  //     "prompt": "Look for code smells in src/ and propose fixes." },
  //   { "group": "Open", "name": "issues", "type": "url",
  //     "url": "https://github.com/<you>/{{projectId}}/issues" }
  // ],

  // Scheduled headless Claude runs. Each entry fires on its cron schedule
  // and starts `claude -p "<prompt>" --output-format json` in this project's
  // directory (the claude CLI must be on the Firepit service's PATH). Each
  // run is recorded under .firepit/runs/<name>/<utc>.json.
  //   name              — unique within this project
  //   prompt            — what gets passed to `claude -p` (slash-commands work)
  //   schedule          — standard 5-field cron expression
  //   enabled           — toggle without deleting (default true)
  //   timeoutSeconds    — kill after N seconds (default 300)
  //   timezone          — IANA tz name (default = local)
  //   onConcurrent      — skip | queue | killAndRestart (default skip)
  //   notify            — always | onChange | never; read but not acted on yet
  //   allowedTools      — passed through as --allowedTools
  //   maxTurns          — passed through as --max-turns
  //   maxBudgetUsd      — passed through as --max-budget-usd
  //   skipPermissions   — --dangerously-skip-permissions (default false)
  // "scheduledJobs": [
  //   { "name": "check-mails", "prompt": "/check-mails",
  //     "schedule": "*/30 * * * *" },
  //   { "name": "weekly-review", "prompt": "/weekly-review",
  //     "schedule": "0 8 * * 1", "timeoutSeconds": 900 }
  // ],

  // How long run records are kept. Null → settings.platform.runRetentionDays.
  // "runs": {
  //   "retentionDays": 30
  // }
}
""";
}
