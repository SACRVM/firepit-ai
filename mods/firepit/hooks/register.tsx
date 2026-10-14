import { atom, read, update } from 'claude-code'
import type { EngineInterface, FsEntry, McpToolResult, Register } from 'claude-code'

import type { Artifact, Cmd, InboxMessage, InboxMode, Loaded } from '../types'
import { clip, fileName, fileUri, isViewable, isWindowsPath, readArtifacts } from './artifacts'
import { ago, deliveryPrompt, handOverPrompt, isMode, MODES, parseMessage, sameMessages, sweep } from './inbox'
import {
  findCommands,
  hashKey,
  inlineArgv,
  launchArgv,
  parseActiveUnits,
  parseJsonc,
  phraseOf,
  readCommands,
  resolveCwd,
  reuseId,
  sections,
  slug,
  trustMaterial,
  unitFor,
} from './logic'

type $ = EngineInterface

/** Shown in the panel and by /fire status, so a stale load is visible. Keep in step with plugin.json. */
const VERSION = '0.6.0'
const PANE = 'firepit'
const VIEWER = 'firepit-viewer'
const CONFIG = '.firepit/config.json'
const INBOX = '.firepit/inbox'
const ARTIFACTS = '.firepit/artifacts.json'
/** The Firepit MCP server, as each project's .mcp.json names it. */
const MCP = 'firepit'
/** Rows a panel section shows before "all". */
const SHOWN = 8

const loaded = atom({ plugin: 'firepit', key: 'loaded' } as const, null)
const running = atom({ plugin: 'firepit', key: 'running' } as const, [])
const isTrusted = atom({ plugin: 'firepit', key: 'isTrusted' } as const, false)
const isReviewing = atom({ plugin: 'firepit', key: 'isReviewing' } as const, false)
const confirming = atom({ plugin: 'firepit', key: 'confirming' } as const, null)
const logsFor = atom({ plugin: 'firepit', key: 'logsFor' } as const, null)
const logs = atom({ plugin: 'firepit', key: 'logs' } as const, '')
const showDetails = atom({ plugin: 'firepit', key: 'showDetails' } as const, false)
const inbox = atom({ plugin: 'firepit', key: 'inbox' } as const, [])
const inboxMode = atom({ plugin: 'firepit', key: 'inboxMode' } as const, 'tell')
const showAllMessages = atom({ plugin: 'firepit', key: 'showAllMessages' } as const, false)
const artifacts = atom({ plugin: 'firepit', key: 'artifacts' } as const, [])
const showAllArtifacts = atom({ plugin: 'firepit', key: 'showAllArtifacts' } as const, false)
const viewing = atom({ plugin: 'firepit', key: 'viewing' } as const, null)

// Module variables start over on a hot reload; nothing drawn depends on them.
let lastRoot = ''
let lastMtime = -1
let launches = 0

// Inbox and artifacts: what was read last, so a sweep that finds nothing new writes nothing.
let inboxRoot = ''
let inboxSignature = ''
const parsedMessages = new Map<string, { mtimeMs: number; message: InboxMessage }>()
let artifactsRoot = ''
let artifactsMtime = -2
let artifactSweeps = 0
let isSweeping = false

// Delivery. The ids that are not news: whatever sat in the inbox when the
// session started (a hot reload starts it over, which only means a message
// that came in during the reload is not handed over), and what was handed over
// since. Turns in flight, by id: a session with one running is not idle.
let handled: Set<string> | null = null
const activeTurns = new Set<string>()
let idleSweeps = 0

/** Runs a press handler's work; a failure becomes a toast, never an unhandled rejection. */
function fire($: $, work: Promise<unknown>): void {
  work.catch((error: unknown) => {
    $.ui.toast(`Firepit commands: ${error instanceof Error ? error.message : String(error)}`)
  })
}

async function sha256(text: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text))
  return [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, '0')).join('')
}

/** The desktop variables a detached unit needs to reach the session: windows, D-Bus, PATH. */
async function desktopEnv($: $): Promise<Record<string, string>> {
  const pairs: [string, string | undefined][] = [
    ['PATH', await $.env.get('PATH')],
    ['DISPLAY', await $.env.get('DISPLAY')],
    ['WAYLAND_DISPLAY', await $.env.get('WAYLAND_DISPLAY')],
    ['XDG_CURRENT_DESKTOP', await $.env.get('XDG_CURRENT_DESKTOP')],
    ['XDG_SESSION_TYPE', await $.env.get('XDG_SESSION_TYPE')],
    ['XDG_RUNTIME_DIR', await $.env.get('XDG_RUNTIME_DIR')],
    ['DBUS_SESSION_BUS_ADDRESS', await $.env.get('DBUS_SESSION_BUS_ADDRESS')],
    ['LANG', await $.env.get('LANG')],
  ]
  const env: Record<string, string> = {}
  for (const [name, value] of pairs) if (value !== undefined && value !== '') env[name] = value
  return env
}

/** Reads the config when the root or the file changed; `force` always reads. */
async function reloadConfig($: $, force: boolean): Promise<void> {
  const root = await $.session.root()
  const path = `${root}/${CONFIG}`
  const stat = await $.fs.stat(path).catch(() => undefined)
  const mtime = stat?.kind === 'file' ? stat.mtimeMs : -1
  if (!force && root === lastRoot && mtime === lastMtime) return
  lastRoot = root
  lastMtime = mtime

  if (mtime < 0) {
    await update($, loaded, () => null)
    return
  }

  const fallbackId = root.split('/').filter(Boolean).pop() ?? 'project'
  let next: Loaded
  try {
    const parsed = readCommands(parseJsonc(await $.fs.read(path)))
    next = {
      root,
      projectId: parsed.id ?? fallbackId,
      commands: parsed.commands,
      problems: parsed.problems,
      hash: await sha256(trustMaterial(parsed.commands)),
    }
  } catch (error) {
    next = {
      root,
      projectId: fallbackId,
      commands: [],
      problems: [`${CONFIG} could not be read: ${error instanceof Error ? error.message : String(error)}`],
      hash: '',
    }
  }

  const hasShell = next.commands.some(c => c.type === 'shell')
  const stored = await $.store.get(`trust:${root}`)
  await update($, loaded, () => next)
  const trusted = !hasShell || stored === next.hash
  if ((await read($, isTrusted)) !== trusted) {
    await update($, isTrusted, () => trusted)
  }
}

async function pollUnits($: $): Promise<void> {
  const cfg = await read($, loaded)
  if (cfg === null) return
  const tracked = cfg.commands.some(c => c.longRunning || reuseId(c.window) !== null)
  if (tracked) {
    const listing = await $.process
      .run(['systemctl', '--user', 'list-units', '--type=service', '--all', '--no-legend', '--plain', '--no-pager', `firepit-${slug(cfg.projectId)}-*`])
      .catch(() => undefined)
    if (listing !== undefined) {
      const active = parseActiveUnits(listing.stdout)
      // Only a change is written. Every write redraws the bar and the panel,
      // and a click that lands on a drawing just replaced is lost — with a
      // write every three seconds, panel buttons missed most clicks.
      if ((await read($, running)).join('\n') !== active.join('\n')) {
        await update($, running, () => active)
      }
    }
  }
  const key = await read($, logsFor)
  if (key !== null) await fetchLogs($, cfg, key)
}

async function fetchLogs($: $, cfg: Loaded, key: string): Promise<void> {
  const cmd = cfg.commands.find(c => c.key === key)
  if (cmd === undefined) return
  const unit = unitFor(cmd, { projectId: cfg.projectId, nonce: '' })
  const result = await $.process
    .run(['journalctl', '--user', '-u', unit, '-o', 'cat', '-n', '60', '--no-pager'])
    .catch(() => undefined)
  const text = result === undefined ? 'journalctl could not be run.' : result.stdout.trimEnd() || 'No output yet.'
  if ((await read($, logs)) !== text) {
    await update($, logs, () => text)
  }
}

async function openPane($: $): Promise<void> {
  await $.ui.open({ id: PANE, title: 'Firepit' })
}

async function showLogs($: $, cmd: Cmd): Promise<void> {
  const cfg = await read($, loaded)
  if (cfg === null) return
  await update($, logsFor, () => cmd.key)
  await fetchLogs($, cfg, cmd.key)
  await openPane($)
}

/** What a run tells the person (a transcript row) and, optionally, Claude. */
type RunResult = { text: string; context?: string[] }

// A confirmation given in the bar, for the one run it was given for. Module
// state is fine here: a hot reload in between only means asking again.
const confirmedOnce = new Set<string>()

const OUTPUT_LINES_SHOWN = 60
const OUTPUT_CHARS_FOR_CLAUDE = 20000

function tailLines(text: string, count: number): string {
  const lines = text.split('\n')
  return lines.length <= count
    ? text
    : [`… ${lines.length - count} earlier lines`, ...lines.slice(-count)].join('\n')
}

/**
 * Runs a command from a button. An inline command goes through `/fire`, so
 * its output lands in the chat as a command row — and in Claude's context,
 * as a `!` command's would — instead of in a side panel.
 */
async function pressCommand($: $, cmd: Cmd, isConfirmed: boolean): Promise<void> {
  if (cmd.type === 'shell' && cmd.window === 'inline') {
    if (isConfirmed) confirmedOnce.add(cmd.key)
    await update($, confirming, () => null)
    await $.command.run({ command: 'fire', args: phraseOf(cmd).toLowerCase() })
    return
  }
  await runCommand($, cmd, isConfirmed)
}

/** Runs one command; resolves what happened, as `/fire` prints it. */
async function runCommand($: $, cmd: Cmd, isConfirmed: boolean): Promise<RunResult> {
  const cfg = await read($, loaded)
  if (cfg === null) return { text: `No ${CONFIG} in this project.` }
  const label = phraseOf(cmd).toLowerCase()

  if (cmd.warning !== null) {
    const text = `${label}: ${cmd.warning} — fix it in ${CONFIG}`
    $.ui.toast(text)
    return { text }
  }
  if (cmd.type === 'shell' && !(await read($, isTrusted))) {
    // The band turns into the review; trusting there runs nothing by itself.
    await update($, isReviewing, () => true)
    return { text: `${label}: not run. Review the project commands above the prompt and trust them first.` }
  }
  if (cmd.confirm && !isConfirmed && !confirmedOnce.delete(cmd.key)) {
    await update($, confirming, () => cmd.key)
    return { text: `${label}: confirm above the prompt.` }
  }
  await update($, confirming, () => null)

  if (cmd.type === 'claude-prompt') {
    await $.prompt.submit({ text: cmd.prompt ?? '', asUser: true })
    return { text: `${label}: sent to Claude.` }
  }
  if (cmd.type === 'url') {
    const opened = await $.process.run(['xdg-open', cmd.url ?? ''], { timeoutMs: 15000 }).catch(() => undefined)
    if (opened === undefined || opened.exitCode !== 0) {
      $.ui.toast(`Could not open ${cmd.url}`)
      return { text: `${label}: could not open ${cmd.url}` }
    }
    return { text: `${label}: opened ${cmd.url}` }
  }

  const env: Record<string, string> = { FIREPIT_PROJECT_NAME: cfg.projectId }
  for (const [k, v] of Object.entries(cmd.env)) if (v !== null) env[k] = v

  if (cmd.window === 'inline') {
    const cwd = resolveCwd(cfg.root, cmd.cwd)
    $.ui.status(`▶ ${label} …`)
    let result: { exitCode: number | null; stdout: string; stderr: string }
    try {
      result = await $.process
        .run(inlineArgv(cmd), { cwd, env, timeoutMs: 600000 })
        .catch((error: unknown) => ({ exitCode: null, stdout: '', stderr: error instanceof Error ? error.message : String(error) }))
    } finally {
      $.ui.status(undefined)
    }
    const output = [result.stdout.trimEnd(), result.stderr.trimEnd()].filter(Boolean).join('\n') || '(no output)'
    const status = result.exitCode === 0 ? 'done' : `exit ${result.exitCode ?? '?'}`
    return {
      text: `${label} · ${status}\n${tailLines(output, OUTPUT_LINES_SHOWN)}`,
      // What `!` gives Claude for a shell command the person ran: it can
      // act on a failing test without being pasted the output.
      context: [
        `The user ran the project command "${label}" (${[cmd.command ?? '', ...cmd.args].join(' ')}) ` +
          `in ${cwd}; it finished with ${status}. Its output` +
          (output.length > OUTPUT_CHARS_FOR_CLAUDE ? ` (last ${OUTPUT_CHARS_FOR_CLAUDE} characters)` : '') +
          `:\n${output.slice(-OUTPUT_CHARS_FOR_CLAUDE)}`,
      ],
    }
  }

  launches += 1
  const ctx = { root: cfg.root, projectId: cfg.projectId, desktopEnv: await desktopEnv($), nonce: `${Date.now().toString(36)}${launches}` }
  const unit = unitFor(cmd, ctx)
  if ((cmd.longRunning || reuseId(cmd.window) !== null) && (await read($, running)).includes(unit)) {
    $.ui.toast(`${label} is already running.`)
    return { text: `${label} is already running.` }
  }
  const started = await $.process.run(launchArgv(cmd, ctx), { timeoutMs: 15000 }).catch(() => undefined)
  if (started === undefined || started.exitCode !== 0) {
    const text = `${label} did not start: ${started?.stderr.trim() || 'systemd-run failed'}`
    $.ui.toast(text)
    return { text }
  }
  if (cmd.longRunning) {
    await update($, running, prev => (prev.includes(unit) ? prev : [...prev, unit]))
    $.ui.toast(`${label} started.`)
    return { text: `${label}: started, logs in the panel.` }
  }
  return { text: `${label}: opened in Konsole.` }
}

async function stopCommand($: $, cmd: Cmd): Promise<void> {
  const cfg = await read($, loaded)
  if (cfg === null) return
  const unit = unitFor(cmd, { projectId: cfg.projectId, nonce: '' })
  const stopped = await $.process.run(['systemctl', '--user', 'stop', unit], { timeoutMs: 20000 }).catch(() => undefined)
  if (stopped === undefined || stopped.exitCode !== 0) $.ui.toast(`Could not stop ${phraseOf(cmd).toLowerCase()}.`)
  await update($, running, prev => prev.filter(u => u !== unit))
}

async function trustCommands($: $): Promise<string> {
  const cfg = await read($, loaded)
  if (cfg === null) return `No ${CONFIG} in this project.`
  await $.store.set(`trust:${cfg.root}`, cfg.hash)
  await update($, isTrusted, () => true)
  await update($, isReviewing, () => false)
  $.ui.toast('Project commands trusted.')
  const shell = cfg.commands.filter(c => c.type === 'shell')
  return [`Trusted ${shell.length} shell commands:`, ...shell.map(c => `  ${phraseOf(c).toLowerCase()} — ${technical(c)}`)].join('\n')
}

async function openFiles($: $): Promise<void> {
  const cfg = await read($, loaded)
  const root = cfg?.root ?? (await $.session.root())
  const opened = await $.process.run(['xdg-open', root], { timeoutMs: 15000 }).catch(() => undefined)
  if (opened === undefined || opened.exitCode !== 0) $.ui.toast('Could not open the file manager.')
}

async function openTerminal($: $): Promise<void> {
  const root = await $.session.root()
  const env = await desktopEnv($)
  launches += 1
  const argv = [
    'systemd-run',
    '--user',
    '--collect',
    '--quiet',
    `--unit=firepit-terminal-${Date.now().toString(36)}${launches}`,
    `--working-directory=${root}`,
    ...Object.entries(env).map(([k, v]) => `--setenv=${k}=${v}`),
    '--',
    'konsole',
    '--workdir',
    root,
  ]
  const started = await $.process.run(argv, { timeoutMs: 15000 }).catch(() => undefined)
  if (started === undefined || started.exitCode !== 0) $.ui.toast('Could not open a terminal.')
}

/** The pending messages, newest last; reads only files that are new or changed. */
async function readInbox($: $, force: boolean): Promise<InboxMessage[]> {
  const root = await $.session.root()
  const dir = `${root}/${INBOX}`
  const entries: readonly FsEntry[] = await $.fs.list(dir).catch(() => [])
  const files = entries
    .filter(f => f.kind === 'file' && f.name.toLowerCase().endsWith('.md'))
    .sort((a, b) => a.name.localeCompare(b.name))
  const signature = files.map(f => `${f.name}:${f.mtimeMs}`).join('\n')
  if (!force && root === inboxRoot && signature === inboxSignature) return read($, inbox)
  if (root !== inboxRoot) parsedMessages.clear()
  inboxRoot = root
  inboxSignature = signature

  const next: InboxMessage[] = []
  for (const f of files) {
    const cached = parsedMessages.get(f.name)
    if (cached !== undefined && cached.mtimeMs === f.mtimeMs) {
      next.push(cached.message)
      continue
    }
    const text = await $.fs.read(`${dir}/${f.name}`).catch(() => null)
    if (typeof text !== 'string') continue
    const { message } = parseMessage(f.name, text)
    parsedMessages.set(f.name, { mtimeMs: f.mtimeMs, message })
    next.push(message)
  }
  for (const name of [...parsedMessages.keys()]) {
    if (!files.some(f => f.name === name)) parsedMessages.delete(name)
  }
  if (!sameMessages(await read($, inbox), next)) await update($, inbox, () => next)
  return next
}

/**
 * Hands new messages to Claude once the session is idle, as the Firepit
 * shell did: the inbox stays the transport, this only says something came.
 */
async function deliverInbox($: $, messages: InboxMessage[]): Promise<void> {
  if (handled === null) {
    handled = new Set(messages.map(m => m.id))
    return
  }
  const known = handled
  // What left the inbox needs no memory; the set stays the inbox's size.
  for (const id of [...known]) if (!messages.some(m => m.id === id)) known.delete(id)
  const fresh = messages.filter(m => !known.has(m.id))
  const mode = await read($, inboxMode)
  if (mode === 'off') {
    // Off means not now and not later: switching on hands over only what comes after.
    for (const m of fresh) known.add(m.id)
    return
  }
  let isIdle = activeTurns.size === 0
  if (isIdle && fresh.length > 0) {
    // Not while the person is typing a prompt of their own.
    const box = await $.prompt.read().catch(() => ({ text: '' }))
    isIdle = box.text.trim() === ''
  }
  const step = sweep({ mode, isIdle, idleSweeps, fresh })
  idleSweeps = step.idleSweeps
  if (step.deliver.length === 0) return
  for (const m of step.deliver) known.add(m.id)
  // Under the plugin's name: Claude reads it as Firepit's notice, not as the person's words.
  const result = await $.prompt.submit({ text: deliveryPrompt(mode, step.deliver) }).catch(() => undefined)
  if (result === undefined || result.drop !== undefined) {
    for (const m of step.deliver) known.delete(m.id)
  }
}

/** `.firepit/artifacts.json`, when it changed, and every 30 s whether the files still exist. */
async function readArtifactLinks($: $, force: boolean): Promise<void> {
  const root = await $.session.root()
  const path = `${root}/${ARTIFACTS}`
  const stat = await $.fs.stat(path).catch(() => undefined)
  const mtime = stat?.kind === 'file' ? stat.mtimeMs : -1
  artifactSweeps += 1
  const isRecheck = artifactSweeps % 15 === 0
  if (!force && !isRecheck && root === artifactsRoot && mtime === artifactsMtime) return
  artifactsRoot = root
  artifactsMtime = mtime

  let next: Artifact[] = []
  if (mtime >= 0) {
    try {
      next = readArtifacts(JSON.parse(String(await $.fs.read(path)).replace(/^﻿/, '')), root)
    } catch {
      next = []
    }
    for (const a of next) {
      a.exists = !isWindowsPath(a.absolute) && (await $.fs.exists(a.absolute).catch(() => false))
    }
  }
  if (JSON.stringify(await read($, artifacts)) !== JSON.stringify(next)) await update($, artifacts, () => next)
}

async function sweepRecords($: $): Promise<void> {
  if (isSweeping) return
  isSweeping = true
  try {
    await deliverInbox($, await readInbox($, false))
    await readArtifactLinks($, false)
  } finally {
    isSweeping = false
  }
}

/** Calls a Firepit tool: `ok`, and what it said (its message, or what went wrong). */
async function callFirepit($: $, tool: string, args: Record<string, unknown>): Promise<{ ok: boolean; message: string }> {
  let result: McpToolResult
  try {
    result = await $.mcp.call(MCP, tool, args)
  } catch (error) {
    return {
      ok: false,
      message: `Firepit did not answer (${error instanceof Error ? error.message : String(error)}). Is its MCP server connected? See /mcp.`,
    }
  }
  const text = result.content.map(c => c.text ?? '').join('')
  if (result.isError) return { ok: false, message: text || `${tool} failed.` }
  try {
    const parsed = JSON.parse(text) as Record<string, unknown>
    const ok = (parsed.ok ?? parsed.Ok) !== false
    const message = parsed.message ?? parsed.Message
    return { ok, message: typeof message === 'string' && message !== '' ? message : ok ? text : `${tool} failed.` }
  } catch {
    // Not JSON: the tool said something in words, which is fine.
    return { ok: true, message: text }
  }
}

/** Calls a Firepit tool; resolves an error to show, or null when it worked. */
async function firepitTool($: $, tool: string, args: Record<string, unknown>): Promise<string | null> {
  const { ok, message } = await callFirepit($, tool, args)
  return ok ? null : message
}

async function setMode($: $, mode: InboxMode): Promise<void> {
  await $.store.set(`inbox-mode:${await $.session.root()}`, mode)
  await update($, inboxMode, () => mode)
}

async function handOver($: $, message: InboxMessage | null): Promise<void> {
  // A press is the person asking, so it goes in as their words.
  await $.prompt.submit({ text: handOverPrompt(message), asUser: true })
}

async function completeMessage($: $, message: InboxMessage): Promise<void> {
  const error = await firepitTool($, 'firepit_inbox_complete', { id: message.id })
  if (error !== null) {
    $.ui.toast(error)
    return
  }
  const shown = await read($, viewing)
  if (shown?.kind === 'message' && shown.message.id === message.id) await closeViewer($)
  await readInbox($, true)
}

async function unpin($: $, artifact: Artifact): Promise<void> {
  const error = await firepitTool($, 'firepit_artifact_remove', { path: artifact.path })
  if (error !== null) {
    $.ui.toast(error)
    return
  }
  const shown = await read($, viewing)
  if (shown?.kind === 'artifact' && shown.artifact.path === artifact.path) await closeViewer($)
  await readArtifactLinks($, true)
}

function title(text: string): string {
  return text.length <= 60 ? text : `${text.slice(0, 59)}…`
}

async function viewMessage($: $, message: InboxMessage): Promise<void> {
  const text = await $.fs.read(`${await $.session.root()}/${INBOX}/${message.id}`).catch(() => null)
  if (typeof text !== 'string') {
    $.ui.toast('That message is no longer in the inbox.')
    await readInbox($, true)
    return
  }
  const { body } = parseMessage(message.id, text)
  await update($, viewing, () => ({ kind: 'message', message, body: clip(body).text }))
  await $.ui.open({ id: VIEWER, title: title(message.subject) })
}

async function openArtifact($: $, artifact: Artifact): Promise<void> {
  if (!artifact.exists) {
    $.ui.toast(`${artifact.label} is missing: ${artifact.path}`)
    return
  }
  if (isViewable(artifact.kind)) {
    const text = await $.fs.read(artifact.absolute).catch(() => null)
    if (typeof text === 'string') {
      await update($, viewing, () => ({ kind: 'artifact', artifact, ...clip(text) }))
      await $.ui.open({ id: VIEWER, title: title(artifact.label) })
      return
    }
  }
  await openExternally($, artifact.absolute)
}

async function openExternally($: $, absolute: string): Promise<void> {
  const opened = await $.process.run(['xdg-open', absolute], { timeoutMs: 15000 }).catch(() => undefined)
  if (opened === undefined || opened.exitCode !== 0) $.ui.toast(`Could not open ${fileName(absolute)}.`)
}

/** Selects the file in the file manager (Dolphin answers ShowItems), else opens its folder. */
async function showInFolder($: $, absolute: string): Promise<void> {
  const shown = await $.process
    .run(
      [
        'dbus-send',
        '--session',
        '--dest=org.freedesktop.FileManager1',
        '--type=method_call',
        '/org/freedesktop/FileManager1',
        'org.freedesktop.FileManager1.ShowItems',
        `array:string:${fileUri(absolute)}`,
        'string:',
      ],
      { timeoutMs: 15000 },
    )
    .catch(() => undefined)
  if (shown !== undefined && shown.exitCode === 0) return
  await openExternally($, absolute.replace(/\/[^/]*$/, '') || '/')
}

async function closeViewer($: $): Promise<void> {
  await update($, viewing, () => null)
  await $.ui.close({ id: VIEWER })
}

async function backToPanel($: $): Promise<void> {
  await closeViewer($)
  await openPane($)
}

function inboxText(messages: InboxMessage[], mode: InboxMode): string {
  const explains = MODES.find(m => m.mode === mode)?.explains ?? ''
  if (messages.length === 0) return `Inbox: empty. New messages: ${mode} — ${explains}`
  return [
    `Inbox: ${messages.length} pending. New messages: ${mode} — ${explains}`,
    ...[...messages].reverse().map(m => `  ${m.priority === 'high' ? '! ' : ''}${m.subject} — from ${m.from}`),
  ].join('\n')
}

function artifactsText(links: Artifact[]): string {
  if (links.length === 0) return 'Artifacts: none pinned.'
  return [
    `Artifacts: ${links.length} pinned.`,
    ...links.map(a => `  ${a.label} — ${a.exists ? a.path : `missing: ${a.path}`}`),
  ].join('\n')
}

/** What actually runs, and how: the panel's small print. */
function technical(cmd: Cmd): string {
  if (cmd.type === 'claude-prompt') return `sends to Claude: ${cmd.prompt ?? ''}`
  if (cmd.type === 'url') return `opens ${cmd.url ?? ''}`
  const flags = [
    cmd.window === 'inline' ? 'output in the chat' : null,
    cmd.longRunning ? 'keeps running' : null,
    reuseId(cmd.window) !== null && !cmd.longRunning ? 'one at a time' : null,
    cmd.keepOpenOnError ? 'window stays open on error' : null,
    cmd.confirm ? 'asks first' : null,
    cmd.cwd !== null ? `in ${cmd.cwd}/` : null,
    cmd.inBar ? null : 'panel only',
  ].filter(Boolean)
  const argv = [cmd.command ?? '', ...cmd.args].join(' ')
  return flags.length > 0 ? `${argv} · ${flags.join(' · ')}` : argv
}

function listOf(commands: Cmd[]): string {
  return commands.map(c => phraseOf(c).toLowerCase()).join(', ')
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    await $.command.register({
      name: 'fire',
      description: 'Firepit: /fire opens the panel, /fire git status runs a command, /fire open <project> switches project; also inbox, artifacts, reload, trust, status',
      argumentHint: '[group] [command]',
      immediate: true,
    })
    await reloadConfig($, true).catch(() => undefined)
    await pollUnits($).catch(() => undefined)

    activeTurns.clear()
    idleSweeps = 0
    const storedMode = await $.store.get(`inbox-mode:${await $.session.root()}`).catch(() => undefined)
    if (isMode(storedMode) && storedMode !== (await read($, inboxMode))) await update($, inboxMode, () => storedMode)
    handled = null
    await sweepRecords($).catch(() => undefined)

    $.clock.every(2000, () => {
      void reloadConfig($, false).catch(() => undefined)
      void sweepRecords($).catch(() => undefined)
    })
    $.clock.every(3000, () => {
      void pollUnits($).catch(() => undefined)
    })

    return next(e)
  })

  // Idle is no turn in flight; a subagent's turns count too.
  on('turn.start', async ($, e, next) => {
    activeTurns.add(e.turnId)
    idleSweeps = 0
    return next(e)
  })
  on('turn.complete', async ($, e, next) => {
    activeTurns.delete(e.turnId)
    return next(e)
  })

  on('command.run', { command: 'fire' }, async ($, e) => {
    const query = e.args.trim()
    if (query === 'trust') return { text: await trustCommands($) }
    if (query === 'status') {
      const cfg = await read($, loaded)
      const active = await read($, running)
      return {
        text: [
          `firepit ${VERSION}`,
          cfg === null ? `No ${CONFIG} in ${await $.session.root()}` : `${cfg.root}/${CONFIG}: ${cfg.commands.length} commands`,
          `trusted: ${(await read($, isTrusted)) ? 'yes' : 'no'}`,
          `running: ${active.length === 0 ? 'nothing' : active.join(', ')}`,
          `inbox: ${(await read($, inbox)).length} pending, new messages: ${await read($, inboxMode)}`,
          `artifacts: ${(await read($, artifacts)).length} pinned`,
        ].join('\n'),
      }
    }
    if (query === 'reload') {
      await reloadConfig($, true)
      await readInbox($, true)
      await readArtifactLinks($, true)
      const cfg = await read($, loaded)
      return { text: cfg === null ? `No ${CONFIG} in this project.` : `Read ${cfg.commands.length} commands.` }
    }
    if (query === 'open' || query.startsWith('open ')) {
      const name = query.slice(4).trim()
      if (name === '') return { text: 'Which project? /fire open vectorizer (a unique start of the name is enough).' }
      return { text: (await callFirepit($, 'firepit_open_project', { projectName: name })).message }
    }
    if (query === 'inbox') return { text: inboxText(await readInbox($, true), await read($, inboxMode)) }
    if (query === 'artifacts') {
      await readArtifactLinks($, true)
      return { text: artifactsText(await read($, artifacts)) }
    }
    if (query === '') {
      await openPane($)
      return { text: 'Firepit panel opened.' }
    }
    const cfg = await read($, loaded)
    if (cfg === null) return { text: `No ${CONFIG} in this project.` }
    const found = findCommands(cfg.commands, query)
    if (found.length === 1) return await runCommand($, found[0], false)

    const group = cfg.commands.filter(c => c.group?.toLowerCase() === query.toLowerCase())
    if (group.length > 0) {
      return { text: `${group[0].group}: ${group.map(c => c.name).join(', ')} — run one, e.g. /fire ${phraseOf(group[0]).toLowerCase()}` }
    }
    if (found.length > 1) return { text: `"${query}" could be: ${listOf(found)}` }
    return { text: `No command matches "${query}". Commands: ${listOf(cfg.commands)}` }
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    // The engine's own word on whether a turn runs; it outranks a turn.complete that never came.
    if (!e.props.isWorking) activeTurns.clear()
    if (e.props.hasSurvey) return next(e)
    const cfg = await read($, loaded)
    const messages = await read($, inbox)
    const commands = cfg?.commands ?? []
    if (commands.length === 0 && messages.length === 0) return next(e)
    const active = await read($, running)
    const trusted = await read($, isTrusted)
    const reviewing = await read($, isReviewing)
    const asking = await read($, confirming)
    const { Box, Text, Button } = $.ui.resolve(e)

    if (cfg !== null && !trusted && reviewing) {
      return (
        <Box flexDirection="column">
          <Text color="warning" bold>
            Shell commands in {CONFIG} are new or changed. They run only once you trust them:
          </Text>
          {cfg.commands
            .filter(c => c.type === 'shell')
            .map(cmd => (
              <Text wrap="truncate-end">
                <Text bold>{phraseOf(cmd)}</Text>
                <Text dimColor> — {technical(cmd)}</Text>
              </Text>
            ))}
          <Box flexDirection="row" columnGap={1}>
            <Button key="trust" variant="primary" onPress={() => fire($, trustCommands($))}>
              Trust
            </Button>
            <Button key="review-later" onPress={() => fire($, update($, isReviewing, () => false))}>
              Not now
            </Button>
          </Box>
        </Box>
      )
    }

    const pending = asking === null ? undefined : commands.find(c => c.key === asking)
    if (pending !== undefined) {
      return (
        <Box flexDirection="row" columnGap={1}>
          <Text color="warning">Run {phraseOf(pending).toLowerCase()}?</Text>
          <Button key="confirm-run" label="Run" variant="primary" onPress={() => fire($, pressCommand($, pending, true))} />
          <Button key="confirm-cancel" label="Cancel" onPress={() => fire($, update($, confirming, () => null))} />
        </Box>
      )
    }

    const button = (cmd: Cmd) => {
      const isRunning = cfg !== null && active.includes(unitFor(cmd, { projectId: cfg.projectId, nonce: '' }))
      const isBlocked = cmd.warning !== null || (cmd.type === 'shell' && !trusted)
      return (
        <Button
          key={`cmd-${cmd.key}`}
          dimColor={isBlocked}
          onPress={() => fire($, isRunning && cmd.longRunning ? showLogs($, cmd) : pressCommand($, cmd, false))}
        >
          {cmd.name}
          {isRunning ? <Text color="success"> ●</Text> : ''}
        </Button>
      )
    }

    const groupRow = (key: string, label: string | null, items: JSX.Element[]) => (
      <Box key={key} flexDirection="row" columnGap={1}>
        {label !== null && <Text bold>{label}:</Text>}
        {items}
      </Box>
    )

    return (
      <Box flexDirection="row" flexWrap="wrap" columnGap={3} rowGap={1}>
        {messages.length > 0 && (
          <Button key="inbox" variant="primary" onPress={() => fire($, openPane($))}>
            Inbox {messages.length}
          </Button>
        )}
        {cfg !== null && !trusted && commands.some(c => c.type === 'shell') && (
          <Button key="review" variant="primary" onPress={() => fire($, update($, isReviewing, () => true))}>
            Review commands
          </Button>
        )}
        {sections(commands.filter(c => c.inBar)).map(section =>
          groupRow(`group-${slug(section.group ?? section.items[0].key)}`, section.group, section.items.map(button)),
        )}
        {/* Files, terminal, inbox and artifacts live in the panel; the bar only leads there. */}
        <Button key="panel" dimColor onPress={() => fire($, openPane($))}>panel</Button>
      </Box>
    )
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const { Box, Text, Button, Code } = $.ui.resolve(e)
    const cfg = await read($, loaded)
    const root = cfg?.root ?? (await $.session.root())
    const project = cfg?.projectId ?? root.split('/').filter(Boolean).pop() ?? 'project'
    const commands = cfg?.commands ?? []
    const active = await read($, running)
    const trusted = await read($, isTrusted)
    const logKey = await read($, logsFor)
    const logText = await read($, logs)
    const details = await read($, showDetails)
    const messages = await read($, inbox)
    const mode = await read($, inboxMode)
    const allMessages = await read($, showAllMessages)
    const links = await read($, artifacts)
    const allLinks = await read($, showAllArtifacts)
    const now = await $.clock.now()
    const hasShell = commands.some(c => c.type === 'shell')
    // What runs is shown when asked for, and always while trust is pending.
    const isTechnical = details || (hasShell && !trusted)
    const logCmd = logKey === null ? undefined : commands.find(c => c.key === logKey)
    const grouped = sections(commands.map(c => (c.group === null ? { ...c, group: 'Other' } : c)))
    const newestFirst = [...messages].reverse()
    const shownMessages = allMessages ? newestFirst : newestFirst.slice(0, SHOWN)
    const shownLinks = allLinks ? links : links.slice(0, SHOWN)

    const row = (cmd: Cmd) => {
      const isRunning = cfg !== null && active.includes(unitFor(cmd, { projectId: cfg.projectId, nonce: '' }))
      const isBlocked = cmd.warning !== null || (cmd.type === 'shell' && !trusted)
      return (
        <Box key={`row-${cmd.key}`} flexDirection="column" paddingLeft={2}>
          <Box flexDirection="row" flexWrap="wrap" columnGap={1}>
            <Button
              key={`run-${cmd.key}`}
              dimColor={isBlocked}
              onPress={() => fire($, isRunning && cmd.longRunning ? showLogs($, cmd) : pressCommand($, cmd, false))}
            >
              {cmd.name}
            </Button>
            {cmd.warning !== null ? (
              <Text color="warning" wrap="wrap">⚠ {cmd.warning}</Text>
            ) : cmd.description !== null ? (
              <Text wrap="wrap">{cmd.description}</Text>
            ) : (
              <Text dimColor wrap="truncate-end">{technical(cmd)}</Text>
            )}
            {cmd.longRunning && (
              <Text color={isRunning ? 'success' : 'inactive'}>{isRunning ? '● running' : '○ stopped'}</Text>
            )}
            {cmd.longRunning && isRunning && (
              <Button key={`stop-${cmd.key}`} onPress={() => fire($, stopCommand($, cmd))}>stop</Button>
            )}
            {cmd.longRunning && (
              <Button key={`logs-${cmd.key}`} dimColor onPress={() => fire($, showLogs($, cmd))}>logs</Button>
            )}
          </Box>
          {isTechnical && cmd.description !== null && (
            <Text dimColor wrap="truncate-end">  {technical(cmd)}</Text>
          )}
        </Box>
      )
    }

    const messageRow = (m: InboxMessage) => {
      const k = hashKey(m.id)
      return (
        <Box key={`msg-${k}`} flexDirection="row" flexWrap="wrap" columnGap={1} paddingLeft={2}>
          {m.priority === 'high' && <Text color="warning" bold>!</Text>}
          <Button key={`msg-open-${k}`} onPress={() => fire($, viewMessage($, m))}>{m.subject}</Button>
          <Text dimColor>
            {m.from}
            {m.sentAt !== null ? ` · ${ago(m.sentAt, now)}` : ''}
          </Text>
          <Button key={`msg-claude-${k}`} dimColor onPress={() => fire($, handOver($, m))}>Claude</Button>
          <Button key={`msg-done-${k}`} dimColor onPress={() => fire($, completeMessage($, m))}>done</Button>
        </Box>
      )
    }

    const linkRow = (a: Artifact) => {
      const k = hashKey(a.path)
      return (
        <Box key={`art-${k}`} flexDirection="column" paddingLeft={2}>
          <Box flexDirection="row" flexWrap="wrap" columnGap={1}>
            <Button key={`art-open-${k}`} dimColor={!a.exists} onPress={() => fire($, openArtifact($, a))}>
              {a.label}
            </Button>
            {a.exists ? <Text dimColor>{a.kind}</Text> : <Text color="warning">missing</Text>}
            {a.exists && (
              <Button key={`art-folder-${k}`} dimColor onPress={() => fire($, showInFolder($, a.absolute))}>folder</Button>
            )}
            <Button key={`art-unpin-${k}`} dimColor onPress={() => fire($, unpin($, a))}>unpin</Button>
          </Box>
          {a.note !== null && <Text dimColor wrap="wrap">  {a.note}</Text>}
          {(details || !a.exists) && <Text dimColor wrap="truncate-start">  {a.path}</Text>}
        </Box>
      )
    }

    const more = (key: string, total: number, isAll: boolean, toggle: () => Promise<unknown>) =>
      total > SHOWN && (
        <Box key={`${key}-row`} flexDirection="row" paddingLeft={2}>
          <Button key={key} dimColor onPress={() => fire($, toggle())}>{isAll ? 'fewer' : `all ${total}`}</Button>
        </Box>
      )

    return (
      <Box flexDirection="column" rowGap={1}>
        <Box flexDirection="column">
          <Box flexDirection="row" columnGap={1}>
            <Text bold>{project}</Text>
            <Text dimColor>v{VERSION}</Text>
            <Button key="p-files" dimColor onPress={() => fire($, openFiles($))}>files</Button>
            <Button key="p-terminal" dimColor onPress={() => fire($, openTerminal($))}>terminal</Button>
            <Button
              key="p-reload"
              dimColor
              onPress={() => fire($, Promise.all([reloadConfig($, true), readInbox($, true), readArtifactLinks($, true)]))}
            >
              reload
            </Button>
            <Button key="p-details" dimColor onPress={() => fire($, update($, showDetails, v => !v))}>
              {details ? 'hide details' : 'details'}
            </Button>
          </Box>
          {cfg !== null ? (
            <Text dimColor wrap="truncate-start">{cfg.root}/{CONFIG}</Text>
          ) : (
            <Text dimColor wrap="wrap">No {CONFIG} here, so no commands.</Text>
          )}
          {(cfg?.problems ?? []).map(p => (
            <Text color="warning" wrap="wrap">⚠ {p}</Text>
          ))}
        </Box>

        {cfg !== null && !trusted && hasShell && (
          <Box flexDirection="column">
            <Text color="warning" bold wrap="wrap">
              Shell commands are new or changed. Nothing runs until you trust them; below each one is what it runs.
            </Text>
            <Box flexDirection="row" columnGap={1}>
              <Button key="trust" variant="primary" onPress={() => fire($, trustCommands($))}>Trust</Button>
            </Box>
          </Box>
        )}

        <Box key="inbox" flexDirection="column">
          <Box flexDirection="row" flexWrap="wrap" columnGap={1}>
            <Text bold>Inbox</Text>
            <Text dimColor>{messages.length === 0 ? 'empty' : `${messages.length} pending`}</Text>
            {messages.length > 1 && (
              <Button key="inbox-all" dimColor onPress={() => fire($, handOver($, null))}>all to Claude</Button>
            )}
          </Box>
          <Box flexDirection="row" flexWrap="wrap" columnGap={1} paddingLeft={2}>
            <Text dimColor>new messages</Text>
            {MODES.map(m =>
              m.mode === mode ? (
                <Button key={`mode-${m.mode}`} variant="primary" onPress={() => fire($, setMode($, m.mode))}>{m.label}</Button>
              ) : (
                <Button key={`mode-${m.mode}`} dimColor onPress={() => fire($, setMode($, m.mode))}>{m.label}</Button>
              ),
            )}
            <Text dimColor wrap="wrap">{MODES.find(m => m.mode === mode)?.explains ?? ''}</Text>
          </Box>
          {shownMessages.map(messageRow)}
          {more('inbox-more', messages.length, allMessages, () => update($, showAllMessages, v => !v))}
        </Box>

        {logCmd !== undefined && (
          <Box flexDirection="column">
            <Box flexDirection="row" columnGap={1}>
              <Text bold>Logs · {phraseOf(logCmd).toLowerCase()}</Text>
              <Button key="logs-close" dimColor onPress={() => fire($, update($, logsFor, () => null))}>hide</Button>
            </Box>
            <Code source={logText} wrap="wrap" />
          </Box>
        )}

        {grouped.map(section => (
          <Box key={`section-${slug(section.group ?? 'other')}`} flexDirection="column">
            <Text bold>{section.group}</Text>
            {section.items.map(row)}
          </Box>
        ))}

        <Box key="artifacts" flexDirection="column">
          <Box flexDirection="row" columnGap={1}>
            <Text bold>Artifacts</Text>
            <Text dimColor>{links.length === 0 ? 'none pinned' : `${links.length} pinned`}</Text>
          </Box>
          {shownLinks.map(linkRow)}
          {more('art-more', links.length, allLinks, () => update($, showAllArtifacts, v => !v))}
        </Box>
      </Box>
    )
  })

  on('ui.render', { component: 'Pane', requestId: VIEWER }, async ($, e) => {
    const { Box, Text, Button, Code, Markdown } = $.ui.resolve(e)
    const shown = await read($, viewing)
    if (shown === null) {
      return <Text dimColor wrap="wrap">Nothing open. Pick a message or an artifact in the Firepit panel.</Text>
    }

    if (shown.kind === 'message') {
      const m = shown.message
      const facts = [
        `from ${m.from}`,
        m.sentAt !== null ? ago(m.sentAt, await $.clock.now()) : '',
        m.priority !== 'normal' ? `${m.priority} priority` : '',
      ].filter(Boolean)
      return (
        <Box flexDirection="column" rowGap={1}>
          <Box flexDirection="column">
            <Text bold wrap="wrap">{m.subject}</Text>
            <Text dimColor>{facts.join(' · ')}</Text>
            <Box flexDirection="row" columnGap={1}>
              <Button key="v-claude" variant="primary" onPress={() => fire($, handOver($, m))}>Hand to Claude</Button>
              <Button key="v-done" onPress={() => fire($, completeMessage($, m))}>Done</Button>
              <Button key="v-back" dimColor onPress={() => fire($, backToPanel($))}>back</Button>
            </Box>
          </Box>
          <Markdown text={shown.body === '' ? '(no text)' : shown.body} />
        </Box>
      )
    }

    const a = shown.artifact
    return (
      <Box flexDirection="column" rowGap={1}>
        <Box flexDirection="column">
          <Text bold wrap="wrap">{a.label}</Text>
          {a.note !== null && <Text wrap="wrap">{a.note}</Text>}
          <Text dimColor wrap="truncate-start">{a.path}</Text>
          <Box flexDirection="row" columnGap={1}>
            <Button key="v-open" onPress={() => fire($, openExternally($, a.absolute))}>Open</Button>
            <Button key="v-folder" dimColor onPress={() => fire($, showInFolder($, a.absolute))}>folder</Button>
            <Button key="v-back" dimColor onPress={() => fire($, backToPanel($))}>back</Button>
          </Box>
          {shown.isCut && <Text color="warning" wrap="wrap">Showing the start only; Open shows all of it.</Text>}
        </Box>
        {a.kind === 'markdown' ? (
          <Markdown text={shown.text === '' ? '(empty)' : shown.text} />
        ) : (
          <Code source={shown.text} path={a.absolute} wrap="wrap" />
        )}
      </Box>
    )
  })
}
