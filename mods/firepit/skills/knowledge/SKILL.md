---
name: knowledge
description: "Keep Firepit's knowledge base useful: search before researching, save durable findings, choose between project and global scope, pin sparingly, correct instead of duplicating, keep research out of public repositories. Use when you learned something worth keeping (a pitfall, a decision and its reason, a platform fact, a workaround), when the user says to remember, note or document something for later, or before researching something that may already be known."
---

# The knowledge base

Markdown documents, searched by meaning across all projects. A project's own
documents live in its `.firepit/knowledge/` (or, for a public repository, in
the central project behind a pointer file); the global base lives in the
central project. Every session can search all of it.

## Search first

Before researching anything that smells decided or solved before:
`firepit_knowledge_search` with `scope: "both"` (this project and the global
base), then `firepit_knowledge_get` for the full document of a hit. A search
that comes back empty but reports a base it could not search is not an
answer; say so.

## What is worth saving

- A pitfall that cost time, with its cause and the way around it.
- A decision and **why** it was made, so nobody reopens it blindly.
- A platform or tool fact you measured, with the version it applies to.
- A procedure that will be needed again and is not obvious from the code.

Not worth a document: what the code, the git history or the README already
say; one-off state; the user's personal preferences. Those belong in Claude
Code's own memory, which is the user's and not shared with other projects.

## Writing one

- `firepit_knowledge_add` with `title` and `content`, in **English**: the index
  embeds English best, and the base is shared.
- The title states the finding, not the topic. "Claude Code aborts silent MCP
  tool calls after 300 s — send progress notifications" finds and explains
  itself; "MCP timeouts" does neither.
- The content: the finding first, then the evidence (what was measured, the
  versions), then what to do. Short is fine, and it should stand alone.
- `scope`: leave it out for this project. Use `"global"` for what applies
  across projects: tools, platforms, libraries, the author's conventions.

## Pinning

`pinned: true` puts a document into every session of its project at start,
through `.firepit/knowledge-pinned.md`. Reserve it for reflex rules an agent
must follow without searching, and keep the pinned set to a handful.
Everything else stays reachable through search.

## Keeping it true

- Found a document that is wrong or stale? `firepit_knowledge_update` with its
  `scope` and `path` from the search hit. Do not add a second one beside it.
- Obsolete? `firepit_knowledge_delete`.
- Editing the markdown file directly works too; Firepit reindexes it.

## Public repositories

Research must not land in a public repository. There `.firepit/knowledge` is a
pointer file, and `firepit_knowledge_add` writes into the central project. If
you find a directory of documents there instead, stop and tell the user
before saving anything. The generated `.firepit/knowledge-pinned.md` is not
committed in a public repository.

## Committing

The documents are plain files. In a private repository commit them with the
work they belong to. Documents in the central project (global scope, or a
public repository's) are files of the central repository at
`<projects root>/.firepit`; commit them there too, or tell the user they are
waiting.
