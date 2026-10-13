import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { Cmd, Loaded, Output } from '../types'
import {
  findCommands,
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
const VERSION = '0.2.0'
const PANE = 'firepit-commands'
const CONFIG = '.firepit/config.json'

const loaded = atom({ plugin: 'firepit-commands', key: 'loaded' } as const, null)
const running = atom({ plugin: 'firepit-commands', key: 'running' } as const, [])
const isTrusted = atom({ plugin: 'firepit-commands', key: 'isTrusted' } as const, false)
const isReviewing = atom({ plugin: 'firepit-commands', key: 'isReviewing' } as const, false)
const confirming = atom({ plugin: 'firepit-commands', key: 'confirming' } as const, null)
const output = atom({ plugin: 'firepit-commands', key: 'output' } as const, null)
const logsFor = atom({ plugin: 'firepit-commands', key: 'logsFor' } as const, null)
const logs = atom({ plugin: 'firepit-commands', key: 'logs' } as const, '')
const showDetails = atom({ plugin: 'firepit-commands', key: 'showDetails' } as const, false)

// Module variables start over on a hot reload; nothing drawn depends on them.
let lastRoot = ''
let lastMtime = -1
let launches = 0

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
  await update($, isTrusted, () => !hasShell || stored === next.hash)
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
      await update($, running, prev => (prev.join('\n') === active.join('\n') ? prev : active))
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
  await update($, logs, prev => (prev === text ? prev : text))
}

async function openPane($: $): Promise<void> {
  await $.ui.open({ id: PANE, title: 'Commands' })
}

async function showLogs($: $, cmd: Cmd): Promise<void> {
  const cfg = await read($, loaded)
  if (cfg === null) return
  await update($, logsFor, () => cmd.key)
  await fetchLogs($, cfg, cmd.key)
  await openPane($)
}

async function setOutput($: $, value: Output): Promise<void> {
  await update($, output, () => value)
}

/** Runs one command; resolves what happened, in words `/fire` can print. */
async function runCommand($: $, cmd: Cmd, isConfirmed: boolean): Promise<string> {
  const cfg = await read($, loaded)
  if (cfg === null) return `No ${CONFIG} in this project.`

  if (cmd.warning !== null) {
    const text = `${phraseOf(cmd).toLowerCase()}: ${cmd.warning} — fix it in ${CONFIG}`
    $.ui.toast(text)
    return text
  }
  if (cmd.type === 'shell' && !(await read($, isTrusted))) {
    // The band turns into the review; trusting there runs nothing by itself.
    await update($, isReviewing, () => true)
    return `${phraseOf(cmd).toLowerCase()}: not run. Review the project commands above the prompt and trust them first.`
  }
  if (cmd.confirm && !isConfirmed) {
    await update($, confirming, () => cmd.key)
    return `${phraseOf(cmd).toLowerCase()}: confirm above the prompt.`
  }
  await update($, confirming, () => null)

  if (cmd.type === 'claude-prompt') {
    await $.prompt.submit({ text: cmd.prompt ?? '', asUser: true })
    return `${phraseOf(cmd).toLowerCase()}: sent to Claude.`
  }
  if (cmd.type === 'url') {
    const opened = await $.process.run(['xdg-open', cmd.url ?? ''], { timeoutMs: 15000 }).catch(() => undefined)
    if (opened === undefined || opened.exitCode !== 0) {
      $.ui.toast(`Could not open ${cmd.url}`)
      return `${phraseOf(cmd).toLowerCase()}: could not open ${cmd.url}`
    }
    return `${phraseOf(cmd).toLowerCase()}: opened ${cmd.url}`
  }

  const env: Record<string, string> = { FIREPIT_PROJECT_NAME: cfg.projectId }
  for (const [k, v] of Object.entries(cmd.env)) if (v !== null) env[k] = v

  if (cmd.window === 'inline') {
    await setOutput($, { title: phraseOf(cmd).toLowerCase(), exitCode: null, text: 'Running…' })
    await openPane($)
    const result = await $.process
      .run(inlineArgv(cmd), { cwd: resolveCwd(cfg.root, cmd.cwd), env, timeoutMs: 600000 })
      .catch((error: unknown) => ({ exitCode: null, stdout: '', stderr: error instanceof Error ? error.message : String(error) }))
    const text = [result.stdout.trimEnd(), result.stderr.trimEnd()].filter(Boolean).join('\n') || '(no output)'
    await setOutput($, { title: phraseOf(cmd).toLowerCase(), exitCode: result.exitCode, text })
    return `${phraseOf(cmd).toLowerCase()}: ${result.exitCode === 0 ? 'done' : `exit ${result.exitCode ?? '?'}`}, output in the panel.`
  }

  launches += 1
  const ctx = { root: cfg.root, projectId: cfg.projectId, desktopEnv: await desktopEnv($), nonce: `${Date.now().toString(36)}${launches}` }
  const unit = unitFor(cmd, ctx)
  if ((cmd.longRunning || reuseId(cmd.window) !== null) && (await read($, running)).includes(unit)) {
    $.ui.toast(`${phraseOf(cmd).toLowerCase()} is already running.`)
    return `${phraseOf(cmd).toLowerCase()} is already running.`
  }
  const started = await $.process.run(launchArgv(cmd, ctx), { timeoutMs: 15000 }).catch(() => undefined)
  if (started === undefined || started.exitCode !== 0) {
    const text = `${phraseOf(cmd).toLowerCase()} did not start: ${started?.stderr.trim() || 'systemd-run failed'}`
    $.ui.toast(text)
    return text
  }
  if (cmd.longRunning) {
    await update($, running, prev => (prev.includes(unit) ? prev : [...prev, unit]))
    $.ui.toast(`${phraseOf(cmd).toLowerCase()} started.`)
    return `${phraseOf(cmd).toLowerCase()}: started, logs in the panel.`
  }
  return `${phraseOf(cmd).toLowerCase()}: opened in Konsole.`
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

/** What actually runs, and how: the panel's small print. */
function technical(cmd: Cmd): string {
  if (cmd.type === 'claude-prompt') return `sends to Claude: ${cmd.prompt ?? ''}`
  if (cmd.type === 'url') return `opens ${cmd.url ?? ''}`
  const flags = [
    cmd.window === 'inline' ? 'output in this panel' : null,
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
      description: 'Firepit project commands: /fire opens the panel, /fire git status runs one; also reload, trust, status',
      argumentHint: '[group] [command]',
      immediate: true,
    })
    await reloadConfig($, true).catch(() => undefined)
    await pollUnits($).catch(() => undefined)
    $.clock.every(2000, () => {
      void reloadConfig($, false).catch(() => undefined)
    })
    $.clock.every(3000, () => {
      void pollUnits($).catch(() => undefined)
    })

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
          `firepit-commands ${VERSION}`,
          cfg === null ? `No ${CONFIG} in ${await $.session.root()}` : `${cfg.root}/${CONFIG}: ${cfg.commands.length} commands`,
          `trusted: ${(await read($, isTrusted)) ? 'yes' : 'no'}`,
          `running: ${active.length === 0 ? 'nothing' : active.join(', ')}`,
        ].join('\n'),
      }
    }
    if (query === 'reload') {
      await reloadConfig($, true)
      const cfg = await read($, loaded)
      return { text: cfg === null ? `No ${CONFIG} in this project.` : `Read ${cfg.commands.length} commands.` }
    }
    const cfg = await read($, loaded)
    if (cfg === null) return { text: `No ${CONFIG} in this project.` }
    if (query === '') {
      await openPane($)
      return { text: 'Commands panel opened.' }
    }
    const found = findCommands(cfg.commands, query)
    if (found.length === 1) return { text: await runCommand($, found[0], false) }

    const group = cfg.commands.filter(c => c.group?.toLowerCase() === query.toLowerCase())
    if (group.length > 0) {
      return { text: `${group[0].group}: ${group.map(c => c.name).join(', ')} — run one, e.g. /fire ${phraseOf(group[0]).toLowerCase()}` }
    }
    if (found.length > 1) return { text: `"${query}" could be: ${listOf(found)}` }
    return { text: `No command matches "${query}". Commands: ${listOf(cfg.commands)}` }
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    if (e.props.hasSurvey) return next(e)
    const cfg = await read($, loaded)
    if (cfg === null || cfg.commands.length === 0) return next(e)
    const active = await read($, running)
    const trusted = await read($, isTrusted)
    const reviewing = await read($, isReviewing)
    const asking = await read($, confirming)
    const { Box, Text, Button } = $.ui.resolve(e)

    if (!trusted && reviewing) {
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

    const pending = asking === null ? undefined : cfg.commands.find(c => c.key === asking)
    if (pending !== undefined) {
      return (
        <Box flexDirection="row" columnGap={1}>
          <Text color="warning">Run {phraseOf(pending).toLowerCase()}?</Text>
          <Button key="confirm-run" label="Run" variant="primary" onPress={() => fire($, runCommand($, pending, true))} />
          <Button key="confirm-cancel" label="Cancel" onPress={() => fire($, update($, confirming, () => null))} />
        </Box>
      )
    }

    const button = (cmd: Cmd) => {
      const isRunning = active.includes(unitFor(cmd, { projectId: cfg.projectId, nonce: '' }))
      const isBlocked = cmd.warning !== null || (cmd.type === 'shell' && !trusted)
      return (
        <Button
          key={`cmd-${cmd.key}`}
          dimColor={isBlocked}
          onPress={() => fire($, isRunning && cmd.longRunning ? showLogs($, cmd) : runCommand($, cmd, false))}
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
        {!trusted && (
          <Button key="review" variant="primary" onPress={() => fire($, update($, isReviewing, () => true))}>
            Review commands
          </Button>
        )}
        {sections(cfg.commands.filter(c => c.inBar)).map(section =>
          groupRow(`group-${slug(section.group ?? section.items[0].key)}`, section.group, section.items.map(button)),
        )}
        {/* Files and terminal live in the panel's head; the bar only leads there. */}
        <Button key="panel" dimColor onPress={() => fire($, openPane($))}>panel</Button>
      </Box>
    )
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const { Box, Text, Button, Code } = $.ui.resolve(e)
    const cfg = await read($, loaded)
    if (cfg === null) {
      return <Text dimColor>No {CONFIG} in this project.</Text>
    }
    const active = await read($, running)
    const trusted = await read($, isTrusted)
    const last = await read($, output)
    const logKey = await read($, logsFor)
    const logText = await read($, logs)
    const details = await read($, showDetails)
    const hasShell = cfg.commands.some(c => c.type === 'shell')
    // What runs is shown when asked for, and always while trust is pending.
    const isTechnical = details || (hasShell && !trusted)
    const logCmd = logKey === null ? undefined : cfg.commands.find(c => c.key === logKey)
    const grouped = sections(cfg.commands.map(c => (c.group === null ? { ...c, group: 'Other' } : c)))

    const row = (cmd: Cmd) => {
      const isRunning = active.includes(unitFor(cmd, { projectId: cfg.projectId, nonce: '' }))
      const isBlocked = cmd.warning !== null || (cmd.type === 'shell' && !trusted)
      return (
        <Box key={`row-${cmd.key}`} flexDirection="column" paddingLeft={2}>
          <Box flexDirection="row" flexWrap="wrap" columnGap={1}>
            <Button
              key={`run-${cmd.key}`}
              dimColor={isBlocked}
              onPress={() => fire($, isRunning && cmd.longRunning ? showLogs($, cmd) : runCommand($, cmd, false))}
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

    return (
      <Box flexDirection="column" rowGap={1}>
        <Box flexDirection="column">
          <Box flexDirection="row" columnGap={1}>
            <Text bold>{cfg.projectId}</Text>
            <Text dimColor>v{VERSION}</Text>
            <Button key="p-files" dimColor onPress={() => fire($, openFiles($))}>files</Button>
            <Button key="p-terminal" dimColor onPress={() => fire($, openTerminal($))}>terminal</Button>
            <Button key="p-reload" dimColor onPress={() => fire($, reloadConfig($, true))}>reload</Button>
            <Button key="p-details" dimColor onPress={() => fire($, update($, showDetails, v => !v))}>
              {details ? 'hide details' : 'details'}
            </Button>
          </Box>
          <Text dimColor wrap="truncate-start">{cfg.root}/{CONFIG}</Text>
          {cfg.problems.map(p => (
            <Text color="warning" wrap="wrap">⚠ {p}</Text>
          ))}
        </Box>

        {!trusted && hasShell && (
          <Box flexDirection="column">
            <Text color="warning" bold wrap="wrap">
              Shell commands are new or changed. Nothing runs until you trust them; below each one is what it runs.
            </Text>
            <Box flexDirection="row" columnGap={1}>
              <Button key="trust" variant="primary" onPress={() => fire($, trustCommands($))}>Trust</Button>
            </Box>
          </Box>
        )}

        {logCmd !== undefined && (
          <Box flexDirection="column">
            <Box flexDirection="row" columnGap={1}>
              <Text bold>Logs · {phraseOf(logCmd).toLowerCase()}</Text>
              <Button key="logs-close" dimColor onPress={() => fire($, update($, logsFor, () => null))}>hide</Button>
            </Box>
            <Code source={logText} wrap="wrap" />
          </Box>
        )}

        {last !== null && (
          <Box flexDirection="column">
            <Box flexDirection="row" columnGap={1}>
              <Text bold>Output · {last.title}</Text>
              <Text color={last.exitCode === null ? 'inactive' : last.exitCode === 0 ? 'success' : 'error'}>
                {last.exitCode === null ? 'running' : `exit ${last.exitCode}`}
              </Text>
              <Button key="output-close" dimColor onPress={() => fire($, update($, output, () => null))}>hide</Button>
            </Box>
            <Code source={last.text} wrap="wrap" />
          </Box>
        )}

        {grouped.map(section => (
          <Box key={`section-${slug(section.group ?? 'other')}`} flexDirection="column">
            <Text bold>{section.group}</Text>
            {section.items.map(row)}
          </Box>
        ))}
      </Box>
    )
  })
}
