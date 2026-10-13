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

export type Output = {
  title: string
  exitCode: number | null
  text: string
}

declare module 'claude-code' {
  interface PluginState {
    'firepit-commands': {
      loaded: Loaded | null
      running: string[]
      isTrusted: boolean
      isReviewing: boolean
      confirming: string | null
      output: Output | null
      logsFor: string | null
      logs: string
      showDetails: boolean
    }
  }
}
