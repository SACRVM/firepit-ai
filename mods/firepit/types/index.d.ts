export type CommandType = 'shell' | 'claude-prompt' | 'url'

/** One command from `.firepit/config.json`, normalised. */
export type Cmd = {
  /** Stable address: the reuse id, else the slugged group and name. */
  key: string
  /** Short, unique within its group; group + name read as a phrase ("Git status"). */
  name: string
  type: CommandType
  group: string | null
  /** One sentence for the panel. */
  description: string | null
  /** False keeps it out of the bar; the panel always lists it. */
  inBar: boolean
  command: string | null
  args: string[]
  /** Relative to the project root, as written. */
  cwd: string | null
  /** A null value unsets the variable. */
  env: Record<string, string | null>
  /** `new`, `inline`, or `reuse:<id>`. */
  window: string
  longRunning: boolean
  keepOpenOnError: boolean
  confirm: boolean
  prompt: string | null
  url: string | null
  /** Why this command cannot run on Linux as written, if it cannot. */
  warning: string | null
}

export type Loaded = {
  root: string
  projectId: string
  commands: Cmd[]
  /** Config-level notes: parse errors, ignored keys. */
  problems: string[]
  /** Hash over the shell commands, for the trust check. */
  hash: string
}

/** One pending message in `.firepit/inbox/`, as its frontmatter names it. */
export type InboxMessage = {
  /** The file name: what `firepit_inbox_complete` takes. */
  id: string
  from: string
  subject: string
  /** `low`, `normal` or `high`, as the sender wrote it. */
  priority: string
  /** ISO time, when the message carries one. */
  sentAt: string | null
}

/**
 * What happens when a message arrives while the session is open: nothing but
 * the badge, Claude summarises it and waits, or Claude does the work.
 */
export type InboxMode = 'off' | 'tell' | 'act'

export type ArtifactKind = 'image' | 'markdown' | 'text' | 'document' | 'executable' | 'archive' | 'other'

/** One link from `.firepit/artifacts.json`. */
export type Artifact = {
  /** As stored: relative to the project root, or absolute. */
  path: string
  /** Where it is on this machine; a Windows path stays as it was. */
  absolute: string
  label: string
  note: string | null
  kind: ArtifactKind
  addedAt: string | null
  exists: boolean
}

/** What the viewer pane shows. */
export type Viewing =
  | { kind: 'message'; message: InboxMessage; body: string }
  | { kind: 'artifact'; artifact: Artifact; text: string; isCut: boolean }

declare module 'claude-code' {
  interface PluginState {
    'firepit': {
      loaded: Loaded | null
      running: string[]
      isTrusted: boolean
      isReviewing: boolean
      confirming: string | null
      logsFor: string | null
      logs: string
      showDetails: boolean
      inbox: InboxMessage[]
      inboxMode: InboxMode
      showAllMessages: boolean
      artifacts: Artifact[]
      showAllArtifacts: boolean
      viewing: Viewing | null
    }
  }
}
