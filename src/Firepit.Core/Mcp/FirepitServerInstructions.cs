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
        Firepit is the user's local platform behind Claude Code: a knowledge base
        shared by all their projects, an inbox for messages between projects, and
        project commands. It never sees this conversation. Anything the user should
        see or keep, you have to show, send, or save explicitly.

        Showing files. When you produce a file the user will want to open — a report,
        screenshot, diagram, generated image, log excerpt, build output — show it as
        you produce it instead of leaving its path in scrollback. Where the client can
        send a file into the conversation or open it beside it (the Claude desktop
        app can), do that. A report or page meant to be read or shared can be
        published as a Claude artifact; that uploads it to claude.ai, so never with
        secrets or private data. Where neither is available, give the full path.

        Inbox. Other projects, and the user, send messages here. Read the pending ones
        with firepit_inbox_list, act on them, then close each with
        firepit_inbox_complete using the entry's id. Firepit may announce new messages
        on its own once the session is idle; do what that notice asks — summarise and
        wait, or handle them — and stop to ask before anything irreversible.

        Knowledge. Before researching something that may already be known, search with
        firepit_knowledge_search (scope "both" covers this project plus the global
        base). Save durable findings with firepit_knowledge_add, written in English —
        the index embeds English best. Correct or retire stale docs with
        firepit_knowledge_update and firepit_knowledge_delete rather than stacking new
        ones on top. When a search comes back with no results but carries a message
        about a base it could not search, that is not an answer — say so instead of
        concluding the subject is unknown.

        Cross-project. firepit_list_projects shows the user's projects, and
        firepit_send_to delivers a message to another project's agent. Address a
        project by the name firepit_list_projects reports for it.

        Procedures. The firepit plugin's skills hold the how-to: firepit:setup
        (a project's Firepit setup), firepit:commands (project commands),
        firepit:messages (writing and handling inbox messages), firepit:knowledge
        (what to save and where). Load the one that fits before doing that work.
        """;
}
