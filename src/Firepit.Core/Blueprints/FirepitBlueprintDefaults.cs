namespace Firepit.Core.Blueprints;

/// <summary>
/// Single source for the content of the built-in "firepit" blueprint. Used
/// twice: <see cref="BlueprintStore.EnsureDefaults"/> seeds it to
/// <c>{metaProject}/blueprints/firepit/</c> as editable data, and the
/// fresh-project scaffold (ProjectScaffolding) applies the same conventions
/// directly so brand-new projects are blueprint-conformant from birth.
/// </summary>
public static class FirepitBlueprintDefaults
{
    public const string DefaultBlueprintName = "firepit";

    public const string Description =
        "Standard Firepit project layout: shared-config git hygiene, " +
        "inbox convention, knowledge base, artifact pane.";

    /// <summary>Idempotency marker for the CLAUDE.md inbox section.</summary>
    public const string InboxSectionMarker = "firepit_inbox_complete";

    public const string InboxSection =
        "## Firepit inbox\n\n" +
        "At the start of a session, read any pending messages in " +
        "`.firepit/inbox/*.md` — cross-project notes Firepit routes here. " +
        "Act on each, then mark it done with the `firepit_inbox_complete` " +
        "MCP tool, passing the message's filename as the `id`.\n";

    /// <summary>Idempotency marker for the CLAUDE.md artifact section.</summary>
    public const string ArtifactsSectionMarker = "firepit_artifact_add";

    // The artifact tools carry good descriptions, but a tool description is
    // only read once the agent is already looking for that tool — nothing ever
    // prompted the thought "I just produced a file the user wants to see". The
    // inbox and knowledge conventions work precisely because they sit in
    // CLAUDE.md and land in every session's context. Artifacts need the same.
    public const string ArtifactsSection =
        "## Firepit artifacts\n\n" +
        "When you produce a file the user will want to open — a report, " +
        "screenshot, diagram, generated image, log excerpt, build output, or " +
        "an executable you built for them to run — pin it with the " +
        "`firepit_artifact_add` MCP tool so it appears in the project's " +
        "paperclip pane. Do this as you produce it, not at the end of the " +
        "session; a path buried in scrollback is a path the user has to hunt " +
        "for. Pinning only links the file — it stays where it is, and " +
        "`firepit_artifact_remove` never deletes it. Check " +
        "`firepit_artifact_list` first so you update an existing entry " +
        "instead of piling up near-duplicates, and unpin what has gone " +
        "stale.\n";

    /// <summary>Idempotency marker for the CLAUDE.md knowledge section.</summary>
    public const string KnowledgeSectionMarker = "firepit_knowledge_search";

    public const string KnowledgeSection =
        "## Firepit knowledge\n\n" +
        "Before researching something that may already be known, query the " +
        "knowledge base with the `firepit_knowledge_search` MCP tool (scope " +
        "`both` covers this project plus the global base). Save durable " +
        "findings with `firepit_knowledge_add` — written in English, per the " +
        "indexing convention. The created markdown files live under " +
        "`.firepit/knowledge/` and are committed like any other file — unless " +
        "this project's `knowledge.storage` routes them into another " +
        "project's store, which is how a public repo keeps research out of " +
        "its own history. The tool's reply says where the file landed; follow " +
        "it rather than assuming.\n";

    public const string KnowledgeReadmePath = ".firepit/knowledge/README.md";

    public const string KnowledgeReadme =
        "# Knowledge\n\n" +
        "Project knowledge base — research notes, background docs, decisions.\n\n" +
        "Conventions:\n\n" +
        "- One markdown file per topic, written in English (the search index\n" +
        "  embeds English best).\n" +
        "- These files are committed — they are part of the project.\n" +
        "- `knowledge.db` next to this folder is the derived search index:\n" +
        "  gitignored, rebuilt automatically from the markdown at any time.\n" +
        "- Search and add via the Firepit MCP tools `firepit_knowledge_search`\n" +
        "  and `firepit_knowledge_add`; correct or retire stale docs with\n" +
        "  `firepit_knowledge_update` / `firepit_knowledge_delete`.\n" +
        "- Docs whose frontmatter says `pin: true` are the always-on tier:\n" +
        "  Firepit compiles them into `.firepit/knowledge-pinned.md`, which the\n" +
        "  CLAUDE.md convention auto-loads into every session. Keep that set\n" +
        "  small — reflex rules only.\n";

    /// <summary>Idempotency marker for the CLAUDE.md pinned-knowledge section —
    /// the digest's import path itself, so any CLAUDE.md that already imports
    /// the digest counts as conformant.</summary>
    public const string PinnedSectionMarker = ".firepit/knowledge-pinned.md";

    public const string PinnedDigestPath = ".firepit/knowledge-pinned.md";

    public const string PinnedSection =
        "## Firepit pinned knowledge\n\n" +
        "@.firepit/knowledge-pinned.md\n\n" +
        "The import above auto-loads the knowledge docs marked `pin: true` in " +
        "their frontmatter — always-on rules that apply every session without " +
        "a search. Firepit regenerates the file from the pinned docs; don't " +
        "edit it directly. Pin/unpin via the pinned flag on " +
        "`firepit_knowledge_add` / `firepit_knowledge_update`, and keep the " +
        "pinned set small — everything else stays reachable through " +
        "`firepit_knowledge_search`.\n";

    /// <summary>Placeholder digest seeded by scaffold/blueprint so the
    /// CLAUDE.md import resolves before Firepit's first index pass. Keep in
    /// sync with PinnedDigest.Compose (Firepit.Knowledge) — drift is harmless
    /// (one rewrite on the next pass) but noisy in git.</summary>
    public const string PinnedDigestSeed =
        "<!-- Generated by Firepit from `pin: true` docs in .firepit/knowledge/. " +
        "Do not edit — edit the pinned docs instead. -->\n\n" +
        "# Pinned knowledge\n\n" +
        "No pinned documents yet. Add `pin: true` to a knowledge doc's " +
        "frontmatter (or pass pinned=true to firepit_knowledge_add) to " +
        "auto-load it here.\n";
}
