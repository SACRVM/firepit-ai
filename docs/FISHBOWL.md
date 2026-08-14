# Fishbowl integration

[Fishbowl](https://github.com/SACRVM/the-fishbowl) is a self-hosted personal memory store with an MCP surface. Wired into Firepit, it gives each project its own isolated memory backend that the agent can read from and write to via `search_memory`, `remember`, `get_memory`, and friends.

## Mental model

One Firepit project ↔ one Fishbowl team. Fishbowl's "team" is the file-boundary unit (`fishbowl-data/teams/{teamId}/team.db` — schema-identical to a personal DB, separate folder); for solo projects it's just a named workspace with a single owner. Firepit calls it a project; Fishbowl calls it a team. Same thing.

Each project gets its own bearer token, bound server-side to its team. The token *is* the scope — there is no per-call `project_id` argument and no `X-Project` header. Switching project = switching token.

## Bootstrap

In your Fishbowl checkout, run:

```bash
dotnet run --project tools/init-project -- <project-slug>
```

The tool creates the team if absent (idempotent — re-running reuses), mints a team-scoped API key with all read/write scopes, and prints both a `cmdkey` line and a Firepit `mcpOverrides` snippet on stderr. Token comes out on stdout for piping.

See [`tools/init-project/README.md`](https://github.com/SACRVM/the-fishbowl/tree/master/tools/init-project) in the Fishbowl repo for flags.

## Wiring it into Firepit

After bootstrap, two edits to `%APPDATA%\Firepit\settings.json`:

**Global registry (`mcpServers`)** — register Fishbowl once, pointing at your local instance with a placeholder credential reference:

```json
"mcpServers": {
  "fishbowl": {
    "displayName": "Fishbowl",
    "description": "Personal memory and notes",
    "transport": "http",
    "url": "https://localhost:7180/mcp",
    "headers": { "Authorization": "Bearer ${cred:firepit/fishbowl-default}" }
  }
}
```

**Per-project override (`projects[].mcpOverrides`)** — swap the auth header to point at the project-specific credential:

```json
{
  "name": "lighthouse",
  "path": "D:\\Code\\lighthouse",
  "mcpServers": ["fishbowl"],
  "mcpOverrides": {
    "fishbowl": {
      "headers": { "Authorization": "Bearer ${cred:firepit/fishbowl-lighthouse}" }
    }
  }
}
```

The credential reference resolves at session-start time against Windows Credential Manager (target `firepit/fishbowl-lighthouse`), which `init-project`'s `cmdkey` line populated. Each project keeps its own credential entry; switching tabs in Firepit means a different bearer flows to Fishbowl, which means a different team's data is visible.

## Per-project quick-link

Fishbowl exposes `/p/<slug>` as a server-side 302 to the team's SPA workspace. The default Firepit quicklink template `https://localhost:7180/p/{projectName}` works as long as `{projectName}` matches the Fishbowl team slug (which is the convention `init-project` enforces).

## Why no per-call `project_id` argument?

The earlier design sketch (see git history of this doc, and Fishbowl's [issue #1](https://github.com/SACRVM/the-fishbowl/issues/1)) imagined an `X-Project` header or a `project_id` tool argument. Both turned out to be redundant: Fishbowl's bearer-token authentication already carries a context binding (`fishbowl_context_type=team`, `fishbowl_context_id=<teamId>`), and the API/MCP layers already enforce that a token bound to team A cannot read team B's data. Adding a per-call argument would have been a soft second scoping mechanism on top of a hard one — strictly worse than just using one bearer per project.

## Note on storage location (v0.5.0+)

Starting with Firepit v0.5.0, per-project Fishbowl wiring (`mcpServers` activation list + `mcpOverrides` headers) lives in `<project>/.firepit/config.json` under `mcpActivations[].headerOverrides`, not in the global `settings.json`. The first launch after upgrading auto-migrates the existing `settings.Projects[].mcpOverrides` entries silently (with a `.bak` archive). The shape is unchanged — just the file moved. Examples above still describe the global-`settings.json` shape because the migration is transparent and existing wiring continues to work; the snippet `init-project` prints stays valid for either location.
