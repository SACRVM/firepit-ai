// Pure logic: no `$`, no I/O. Everything here is unit-tested.

import type { Cmd, CommandType } from '../types'

/** JSONC → JSON: drops // and /* *\/ comments outside strings, then trailing commas. */
export function parseJsonc(text: string): unknown {
  let out = ''
  let i = 0
  let inString = false
  while (i < text.length) {
    const c = text[i]
    if (inString) {
      out += c
      if (c === '\\' && i + 1 < text.length) {
        out += text[i + 1]
        i += 2
        continue
      }
      if (c === '"') inString = false
      i += 1
      continue
    }
    if (c === '"') {
      inString = true
      out += c
      i += 1
      continue
    }
    if (c === '/' && text[i + 1] === '/') {
      while (i < text.length && text[i] !== '\n') i += 1
      continue
    }
    if (c === '/' && text[i + 1] === '*') {
      const end = text.indexOf('*/', i + 2)
      i = end < 0 ? text.length : end + 2
      continue
    }
    out += c
    i += 1
  }
  return JSON.parse(out.replace(/,(\s*[}\]])/g, '$1'))
}

/** Lowercase, `[a-z0-9-]` only, no leading/trailing dash, at most 40 chars. */
export function slug(text: string): string {
  const s = text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 40)
    .replace(/-+$/g, '')
  return s === '' ? 'x' : s
}

export function unitName(projectId: string, key: string): string {
  return `firepit-${slug(projectId)}-${slug(key)}`
}

/** The id after `reuse:`, or null. */
export function reuseId(window: string): string | null {
  return window.startsWith('reuse:') && window.length > 6 ? window.slice(6) : null
}

const TYPES: Record<string, CommandType> = {
  shell: 'shell',
  'claude-prompt': 'claude-prompt',
  url: 'url',
}

const WINDOWS_PROGRAMS = /^(powershell|pwsh|cmd|explorer|wt)(\.exe)?$/i

/** Why a shell command won't run on Linux as written, or null. */
export function windowsWarning(command: string | null, args: string[], cwd: string | null): string | null {
  if (command !== null && WINDOWS_PROGRAMS.test(command)) return `${command} is a Windows program`
  const all = [command ?? '', cwd ?? '', ...args]
  if (all.some(a => /\.(exe|bat|cmd|ps1)$/i.test(a))) return 'runs a Windows executable or script'
  if (all.some(a => /^[A-Za-z]:[\\/]/.test(a))) return 'uses a Windows drive path'
  if ((command ?? '').includes('\\') || (cwd ?? '').includes('\\')) return 'uses backslashes in a path'
  return null
}

function str(v: unknown): string | null {
  return typeof v === 'string' && v !== '' ? v : null
}

/** Reads `commands` (and legacy `quickLinks`, as the Links group); skips disabled ones, notes what it cannot use. */
export function readCommands(config: unknown): { id: string | null; commands: Cmd[]; problems: string[] } {
  const problems: string[] = []
  const root = (typeof config === 'object' && config !== null ? config : {}) as Record<string, unknown>
  const id = str(root.id)
  const links = (Array.isArray(root.quickLinks) ? root.quickLinks : []).map(link =>
    typeof link === 'object' && link !== null ? { ...(link as Record<string, unknown>), type: 'url', group: 'Links' } : link,
  )
  const raw = [...(Array.isArray(root.commands) ? root.commands : []), ...links]
  const commands: Cmd[] = []
  const seen = new Set<string>()
  const phrases = new Set<string>()

  for (const item of raw) {
    if (typeof item !== 'object' || item === null) continue
    const c = item as Record<string, unknown>
    if (c.disabled === true) continue
    const name = str(c.name)
    const type = TYPES[String(c.type)]
    if (name === null || type === undefined) {
      problems.push(`skipped a command without a valid name or type (${JSON.stringify(c.name ?? c.type ?? '?')})`)
      continue
    }
    const group = str(c.group)
    const phrase = `${group ?? ''} ${name}`.trim()
    if (phrases.has(phrase.toLowerCase())) problems.push(`"${phrase}" is defined twice; names must be unique within a group`)
    phrases.add(phrase.toLowerCase())

    const window = str(c.window) ?? 'new'
    let key = reuseId(window) ?? slug(phrase)
    while (seen.has(key)) key = `${key}-2`
    seen.add(key)

    const args = Array.isArray(c.args) ? c.args.filter((a): a is string => typeof a === 'string') : []
    const env: Record<string, string | null> = {}
    if (typeof c.env === 'object' && c.env !== null) {
      for (const [k, v] of Object.entries(c.env as Record<string, unknown>)) {
        env[k] = typeof v === 'string' ? v : null
      }
    }
    const command = str(c.command)
    const cwd = str(c.cwd)
    let warning = type === 'shell' ? windowsWarning(command, args, cwd) : null
    if (type === 'shell' && command === null) warning = 'has no command'
    if (type === 'claude-prompt' && str(c.prompt) === null) warning = 'has no prompt'
    if (type === 'url' && str(c.url) === null) warning = 'has no url'
    if (c.elevated === true) problems.push(`"${phrase}": elevated is ignored on Linux`)

    commands.push({
      key,
      name,
      type,
      group,
      description: str(c.description),
      inBar: c.bar !== false,
      command,
      args,
      cwd,
      env,
      window,
      longRunning: c.longRunning === true,
      keepOpenOnError: c.keepOpenOnError === true,
      confirm: c.confirm === true,
      prompt: str(c.prompt),
      url: str(c.url),
      warning,
    })
  }
  return { id, commands, problems }
}

/** "Git status": the group and the name, as the person reads them. */
export function phraseOf(cmd: Cmd): string {
  return cmd.group === null ? cmd.name : `${cmd.group} ${cmd.name}`
}

/** Groups in order of first appearance; ungrouped commands each stand alone. */
export function sections(commands: Cmd[]): { group: string | null; items: Cmd[] }[] {
  const out: { group: string | null; items: Cmd[] }[] = []
  for (const cmd of commands) {
    const same = cmd.group === null ? undefined : out.find(s => s.group === cmd.group)
    if (same === undefined) out.push({ group: cmd.group, items: [cmd] })
    else same.items.push(cmd)
  }
  return out
}

/** What the trust check covers: everything about a shell command that decides what runs. */
export function trustMaterial(commands: Cmd[]): string {
  return JSON.stringify(
    commands
      .filter(c => c.type === 'shell')
      .map(c => [c.name, c.command, c.args, c.cwd, Object.entries(c.env).sort(), c.window, c.longRunning]),
  )
}

/** Joins a path relative to the root; an absolute path stays as it is. */
export function resolveCwd(root: string, cwd: string | null): string {
  if (cwd === null || cwd === '.' || cwd === '') return root
  if (cwd.startsWith('/')) return cwd
  return `${root.replace(/\/+$/, '')}/${cwd.replace(/^\.\//, '')}`
}

export type LaunchContext = {
  root: string
  projectId: string
  /** The session's desktop variables (PATH, display, D-Bus), forwarded into the unit. */
  desktopEnv: Record<string, string>
  /** Makes a `new` window's unit name unique. */
  nonce: string
}

/** Keeps a failed command's window open long enough to read. */
const KEEP_OPEN =
  '"$@"; rc=$?; if [ "$rc" -ne 0 ]; then printf "\\n[%s exited with %s] Press Enter to close." "$1" "$rc"; read -r _; fi; exit "$rc"'

/** `env -u` for each variable the command unsets. */
function unsets(cmd: Cmd): string[] {
  return Object.entries(cmd.env)
    .filter(([, v]) => v === null)
    .flatMap(([k]) => ['-u', k])
}

/** The unit a shell command runs under: shared per reuse id, unique per `new` click. */
export function unitFor(cmd: Cmd, ctx: Pick<LaunchContext, 'projectId' | 'nonce'>): string {
  const reuse = reuseId(cmd.window)
  if (reuse !== null) return unitName(ctx.projectId, reuse)
  if (cmd.longRunning) return unitName(ctx.projectId, cmd.key)
  return `${unitName(ctx.projectId, cmd.key)}-${ctx.nonce}`
}

/**
 * The argv that starts a non-inline shell command detached, as a transient
 * systemd user unit: long-running ones run bare (logs in the journal),
 * windowed ones in a Konsole window.
 */
export function launchArgv(cmd: Cmd, ctx: LaunchContext): string[] {
  const unit = unitFor(cmd, ctx)
  const setenv = [
    ...Object.entries(ctx.desktopEnv).map(([k, v]) => `--setenv=${k}=${v}`),
    `--setenv=FIREPIT_PROJECT_NAME=${ctx.projectId}`,
    ...Object.entries(cmd.env)
      .filter((e): e is [string, string] => e[1] !== null)
      .map(([k, v]) => `--setenv=${k}=${v}`),
  ]
  const head = [
    'systemd-run',
    '--user',
    '--collect',
    '--quiet',
    `--unit=${unit}`,
    `--description=Firepit: ${ctx.projectId} / ${cmd.name}`,
    `--working-directory=${resolveCwd(ctx.root, cmd.cwd)}`,
    ...setenv,
    '--',
  ]
  const program = [cmd.command ?? 'true', ...cmd.args]
  if (cmd.longRunning) return [...head, '/usr/bin/env', ...unsets(cmd), ...program]
  const inWindow = cmd.keepOpenOnError ? ['/bin/bash', '-c', KEEP_OPEN, 'firepit', ...program] : program
  return [
    ...head,
    'konsole',
    '--workdir',
    resolveCwd(ctx.root, cmd.cwd),
    '-e',
    '/usr/bin/env',
    ...unsets(cmd),
    ...inWindow,
  ]
}

/** The argv of an inline command (run in the background, output to the panel). */
export function inlineArgv(cmd: Cmd): string[] {
  const u = unsets(cmd)
  const program = [cmd.command ?? 'true', ...cmd.args]
  return u.length > 0 ? ['/usr/bin/env', ...u, ...program] : program
}

/** Active unit names out of `systemctl --user list-units --plain --no-legend`. */
export function parseActiveUnits(listing: string): string[] {
  return listing
    .split('\n')
    .map(line => line.trim().split(/\s+/))
    .filter(cols => cols.length >= 3 && cols[0].endsWith('.service') && (cols[2] === 'active' || cols[2] === 'activating'))
    .map(cols => cols[0].replace(/\.service$/, ''))
}

/**
 * Every command a `/fire` query can mean: "git status", "git:status",
 * "git/status", a phrase, a key, a bare name, or a prefix of one. Exact
 * matches win over prefixes; more than one result is an ambiguity to show.
 */
export function findCommands(commands: Cmd[], query: string): Cmd[] {
  const q = query.trim().toLowerCase().replace(/[:/]+/g, ' ').replace(/\s+/g, ' ')
  if (q === '') return []
  const exact = commands.filter(
    c => phraseOf(c).toLowerCase() === q || c.key === slug(q) || c.name.toLowerCase() === q,
  )
  if (exact.length > 0) return exact
  return commands.filter(c => phraseOf(c).toLowerCase().startsWith(q) || c.name.toLowerCase().startsWith(q))
}

/** A short, stable element key for a long name, such as an inbox file. */
export function hashKey(text: string): string {
  let h = 5381
  for (let i = 0; i < text.length; i += 1) h = ((h * 33) ^ text.charCodeAt(i)) >>> 0
  return h.toString(36)
}
