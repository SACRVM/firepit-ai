namespace Firepit.Core.Mcp;

/// <summary>
/// The text Firepit's MCP server returns in the <c>instructions</c> field of
/// its <c>initialize</c> result.
/// </summary>
/// <remarks>
/// This is the primary channel for Firepit's conventions. The equivalent
/// CLAUDE.md sections in <c>FirepitBlueprintDefaults</c> stay as a fallback for
/// agents that ignore the field, but they have two flaws this channel does not:
/// they are copied into every project (so they go stale, hence
/// <c>BlueprintStore.TopUpSeededSections</c>) and they are committed (so a
/// public repo carries them). Instructions ship inside the executable, reach
/// every project at handshake, and leave no trace in the repo.
///
/// This lands in every session's context, so it is deliberately short. It
/// covers conventions an agent cannot infer from tool descriptions alone —
/// a tool description is only read once the agent is already looking for that
/// tool, which never happens for a habit nobody prompted.
/// </remarks>
public static class FirepitServerInstructions
{
    public const string Text =
        """
        Firepit is the desktop shell hosting this session: one tab per project, an
        artifact pane for pinned files, an inbox for cross-project messages, and a
        searchable knowledge base. Firepit is a transparent host — it never reads or
        interprets terminal output. Anything the user should see or keep, you have to
        pin, send, or save explicitly.

        Artifacts. When you produce a file the user will want to open — a report,
        screenshot, diagram, generated image, log excerpt, build output, or an
        executable for them to run — pin it with firepit_artifact_add as you produce
        it, not at the end of the session. A path buried in scrollback is a path the
        user has to hunt for. Pinning only links the file: it stays where it is, and
        firepit_artifact_remove never deletes it. Call firepit_artifact_list first so
        you update an existing entry instead of piling up near-duplicates, and unpin
        what has gone stale.

        Inbox. Other projects, and the user, send messages here. Read the pending ones
        with firepit_inbox_list, act on them, then close each with
        firepit_inbox_complete using the entry's id. Firepit may hand you a message on
        its own once a session goes idle — treat it as a request from the user, and
        stop to ask before anything irreversible.

        Knowledge. Before researching something that may already be known, search with
        firepit_knowledge_search (scope "both" covers this project plus the global
        base). Save durable findings with firepit_knowledge_add, written in English —
        the index embeds English best. Correct or retire stale docs with
        firepit_knowledge_update and firepit_knowledge_delete rather than stacking new
        ones on top.

        Cross-project. firepit_list_projects shows the user's projects, and
        firepit_send_to delivers a message to another project's agent. Address a
        project by the name firepit_list_projects reports for it.
        """;
}
