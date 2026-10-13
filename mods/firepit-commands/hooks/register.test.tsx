import type { On } from 'claude-code'
import { expect, mock, test } from 'claude-code/testing'
import type { MockClock } from 'claude-code/testing'

import { hashKey } from './logic'

const ROOT = '/r/firepit-template'
const INBOX = `${ROOT}/.firepit/inbox`
const ARTIFACTS = `${ROOT}/.firepit/artifacts.json`

const CONFIG = JSON.stringify({
  id: 'firepit-template',
  commands: [
    { name: 'Site', description: 'Serves the site.', type: 'shell', group: 'Run', command: 'python3', args: ['-m', 'http.server'], window: 'reuse:site', longRunning: true },
    { name: 'Clean', type: 'shell', command: 'rm', args: ['-rf', 'output'], confirm: true },
    { name: 'Ask', type: 'claude-prompt', prompt: 'Summarize.' },
    { name: 'status', group: 'Git', type: 'shell', command: 'git', args: ['status'], window: 'inline' },
  ],
})

const OLD_MESSAGE = '2026-10-13T08-00-00Z-from-alpha-old-news.md'
const NEW_MESSAGE = '2026-10-13T09-00-00Z-from-beta-fresh-news.md'

function messageFile(from: string, subject: string): string {
  return `---\nfrom: ${from}\nto: firepit-template\nsubject: ${subject}\nsentAt: 2026-10-13T08:00:00Z\npriority: normal\n---\n\nPlease **re-vendor**.\n`
}

const BAND = {
  component: 'AbovePrompt',
  props: { hasSurvey: false, isWorking: false, maxRows: 6, bodyColumns: 100, scroll: { offset: 0, bodyRows: 6 }, view: {} },
} as const

const PANE_PROPS = { title: 'Firepit', isFocused: false, bodyColumns: 80, placement: 'dock', scroll: { offset: 0, bodyRows: 40 } } as const

type Ran = string[][]

/** What the world beneath the plugin holds and records. */
type World = {
  ran: Ran
  submitted: { text: string; asUser: boolean }[]
  tools: { tool: string; args: Record<string, unknown> }[]
  files: Map<string, string>
  draft: string
  clock: MockClock
}

/** The world beneath the plugin: one project on disk, systemd and Firepit answering, a store in memory. */
function world(on: On, ran: Ran = [], submitted: string[] = []): World {
  const w: World = {
    ran,
    submitted: [],
    tools: [],
    files: new Map([[`${ROOT}/.firepit/config.json`, CONFIG]]),
    draft: '',
    clock: mock.clock(on, { now: Date.parse('2026-10-13T10:00:00Z') }),
  }
  const store = new Map<string, unknown>()
  on('session.root', () => ({ value: ROOT }))
  on('session.start', (_$, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: undefined }))
  on('fs.stat', (_$, e) => {
    const text = w.files.get(e.path)
    return text === undefined ? { deny: 'ENOENT' } : { value: { kind: 'file', size: text.length, mtimeMs: text.length, isLink: false } }
  })
  on('fs.read', (_$, e) => {
    const text = w.files.get(e.path)
    return text === undefined ? { deny: 'ENOENT' } : { value: text }
  })
  on('fs.exists', (_$, e) => ({ value: w.files.has(e.path) }))
  on('fs.list', (_$, e) => {
    const prefix = `${e.path}/`
    const entries = [...w.files.entries()]
      .filter(([path]) => path.startsWith(prefix) && !path.slice(prefix.length).includes('/'))
      .map(([path, text]) => ({ name: path.slice(prefix.length), kind: 'file', size: text.length, mtimeMs: text.length, isLink: false }))
    return entries.length === 0 ? { deny: 'ENOENT' } : { value: entries }
  })
  on('mcp.call', (_$, e) => {
    w.tools.push({ tool: e.tool, args: e.args })
    if (e.tool === 'firepit_inbox_complete') w.files.delete(`${INBOX}/${String(e.args.id)}`)
    return { value: { content: [{ type: 'text', text: '{"ok":true}' }], isError: false } }
  })
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('ui.close', () => ({ value: undefined }))
  on('ui.toast', () => ({ value: undefined }))
  on('ui.status', () => ({ value: undefined }))
  on('store.get', (_$, e) => ({ value: store.get(e.key) }))
  on('store.set', (_$, e) => {
    store.set(e.key, e.value)
    return { value: undefined }
  })
  on('env.get', (_$, e) => ({ value: e.name === 'PATH' ? '/usr/bin' : undefined }))
  on('process.run', (_$, e) => {
    ran.push([...e.argv])
    const stdout = e.argv[0] === 'git' ? '## main...origin/main\n?? new.txt' : ''
    return { value: { exitCode: 0, stdout, stderr: '', isStdoutTruncated: false, isStderrTruncated: false } }
  })
  on('prompt.read', () => ({ value: { text: w.draft, cursor: w.draft.length } }))
  on('turn.start', (_$, e) => ({ turnId: e.turnId }))
  on('turn.complete', (_$, e) => ({ text: e.answer }))
  on('prompt.submit', (_$, e) => {
    submitted.push(e.text)
    w.submitted.push({ text: e.text, asUser: e.origin.kind === 'plugin' && e.origin.asUser === true })
    return { text: e.text }
  })
  return w
}

for (const surface of ['desktop', 'terminal'] as const) {
  test(`${surface}: untrusted shell commands go to review, trusted ones start under systemd`, async ($, on) => {
    const ran: Ran = []
    const submitted: string[] = []
    world(on, ran, submitted)
    await $.command.run({ command: 'fire', args: 'reload' })

    const ui = await $.ui.mount({ plugin: 'firepit-commands', surface, ...BAND })
    expect(await ui.find({ key: 'review' })).toBeDefined()
    expect(await ui.find({ key: 'cmd-site' })).toBeDefined()

    // Untrusted: pressing a shell command starts nothing and turns the band into the review.
    await ui.press({ key: 'cmd-site' })
    expect(ran.some(argv => argv[0] === 'systemd-run')).toBe(false)
    expect(await ui.find({ key: 'trust' })).toBeDefined()

    // Not now folds it back; the review button opens it again.
    await ui.press({ key: 'review-later' })
    expect(await ui.find({ key: 'trust' })).toBe(undefined)
    await ui.press({ key: 'review' })

    // Trust in the band: the review and its button are gone.
    await ui.press({ key: 'trust' })
    expect(await ui.find({ key: 'review' })).toBe(undefined)

    // A prompt needs no trust.
    await ui.press({ key: 'cmd-ask' })
    expect(submitted).toEqual(['Summarize.'])

    // A trusted shell command starts as a unit.
    await ui.press({ key: 'cmd-site' })
    const start = ran.find(argv => argv[0] === 'systemd-run')
    expect(start).toBeDefined()
    expect(start).toContain('--unit=firepit-firepit-template-site')

    // The panel lists the same groups as the bar, with every command.
    const pane = await $.ui.mount({ plugin: 'firepit-commands', surface, component: 'Pane', requestId: 'firepit-commands', props: PANE_PROPS })
    expect(await pane.find({ type: 'Text', text: 'Run' })).toBeDefined()
    expect(await pane.find({ type: 'Text', text: 'Other' })).toBeDefined()
    expect(await pane.find({ key: 'run-site' })).toBeDefined()
    // Trusted: one line per command, what it runs only on request.
    expect(await pane.find({ type: 'Text', text: /http\.server/ })).toBe(undefined)
    await pane.press({ key: 'p-details' })
    expect(await pane.find({ type: 'Text', text: /http\.server/ })).toBeDefined()
    await pane.unmount()

    // confirm: the first press asks, the band's Run runs it.
    await ui.press({ key: 'cmd-clean' })
    expect(await ui.find({ key: 'confirm-run' })).toBeDefined()
    await ui.press({ key: 'confirm-run' })
    expect(ran.some(argv => argv.includes('rm'))).toBe(true)

    await ui.unmount()
  })
}

test('/fire trust lists what it trusts; /fire status shows the loaded version', async ($, on) => {
  world(on)
  await $.command.run({ command: 'fire', args: 'reload' })
  const before = await $.command.run({ command: 'fire', args: 'status' })
  expect(before.text).toContain('firepit-commands 0.4.0')
  expect(before.text).toContain('trusted: no')
  const trusted = await $.command.run({ command: 'fire', args: 'trust' })
  expect(trusted.text).toContain('Trusted 3 shell commands:')
  expect(trusted.text).toContain('run site — python3 -m http.server')
  const after = await $.command.run({ command: 'fire', args: 'status' })
  expect(after.text).toContain('trusted: yes')
})

test('an inline command answers in the chat, and Claude gets the output too', async ($, on) => {
  const ran: Ran = []
  world(on, ran)
  await $.command.run({ command: 'fire', args: 'reload' })
  await $.command.run({ command: 'fire', args: 'trust' })

  const result = await $.command.run({ command: 'fire', args: 'git status' })

  expect(result.text).toContain('git status · done')
  expect(result.text).toContain('?? new.txt')
  expect(result.context?.[0]).toContain('"git status" (git status)')
  expect(result.context?.[0]).toContain('?? new.txt')
  expect(ran.some(argv => argv[0] === 'git')).toBe(true)
})

for (const surface of ['desktop', 'terminal'] as const) {
  test(`${surface}: the inbox shows in the bar and the panel; done and unpin go through Firepit`, async ($, on) => {
    const w = world(on)
    w.files.set(`${INBOX}/${OLD_MESSAGE}`, messageFile('alpha', 'Old news'))
    w.files.set(ARTIFACTS, JSON.stringify({ version: 1, artifacts: [{ path: 'docs/report.md', label: 'Report', note: 'The weekly one', addedAtUtc: '2026-10-13T09:00:00Z' }, { path: 'C:\\x\\gone.sql', addedAtUtc: '2026-09-01T00:00:00Z' }] }))
    w.files.set(`${ROOT}/docs/report.md`, '# Report\n\nAll good.')
    await $.session.start({ cwd: ROOT, surface, isInteractive: true })

    const band = await $.ui.mount({ plugin: 'firepit-commands', surface, ...BAND })
    expect(await band.find({ key: 'inbox' })).toBeDefined()

    const pane = await $.ui.mount({ plugin: 'firepit-commands', surface, component: 'Pane', requestId: 'firepit-commands', props: PANE_PROPS })
    const k = hashKey(OLD_MESSAGE)
    expect(await pane.find({ key: `msg-open-${k}` })).toBeDefined()
    expect(await pane.find({ type: 'Text', text: '1 pending' })).toBeDefined()
    expect(await pane.find({ key: 'mode-tell' })).toBeDefined()
    expect(await pane.find({ key: `art-open-${hashKey('docs/report.md')}` })).toBeDefined()
    // A Windows link cannot exist here: it says so and offers only unpin.
    expect(await pane.find({ type: 'Text', text: 'missing' })).toBeDefined()
    expect(await pane.find({ key: `art-folder-${hashKey('C:\\x\\gone.sql')}` })).toBe(undefined)

    // A message opens in the viewer, with its body.
    await pane.press({ key: `msg-open-${k}` })
    const viewer = await $.ui.mount({ plugin: 'firepit-commands', surface, component: 'Pane', requestId: 'firepit-viewer', props: { ...PANE_PROPS, title: 'Old news' } })
    expect(await viewer.find({ type: 'Markdown', text: /re-vendor/ })).toBeDefined()

    // Hand to Claude goes in as the person's own request.
    await viewer.press({ key: 'v-claude' })
    expect(w.submitted.at(-1)?.asUser).toBe(true)
    expect(w.submitted.at(-1)?.text).toContain(`(id ${OLD_MESSAGE})`)

    // Done is Firepit's tool; the inbox empties and the bar button goes.
    await viewer.press({ key: 'v-done' })
    expect(w.tools).toContainEqual({ tool: 'firepit_inbox_complete', args: { id: OLD_MESSAGE } })
    expect(await band.find({ key: 'inbox' })).toBe(undefined)
    expect(await pane.find({ type: 'Text', text: 'empty' })).toBeDefined()

    // A markdown artifact opens in the viewer; unpin is Firepit's tool too.
    await pane.press({ key: `art-open-${hashKey('docs/report.md')}` })
    expect(await viewer.find({ type: 'Markdown', text: /All good/ })).toBeDefined()
    await pane.press({ key: `art-unpin-${hashKey('C:\\x\\gone.sql')}` })
    expect(w.tools).toContainEqual({ tool: 'firepit_artifact_remove', args: { path: 'C:\\x\\gone.sql' } })

    await viewer.unmount()
    await pane.unmount()
    await band.unmount()
  })
}

test('a message that arrives while the session waits is handed to Claude once, under the plugin', async ($, on) => {
  const w = world(on)
  const clock = w.clock
  w.files.set(`${INBOX}/${OLD_MESSAGE}`, messageFile('alpha', 'Old news'))
  await $.session.start({ cwd: ROOT, surface: 'desktop', isInteractive: true })

  // What was there at the start is not news.
  await clock.advance(10000)
  expect(w.submitted).toEqual([])

  // A new one, while a turn runs: nothing yet.
  await $.turn.start({ text: 'work', turnId: 't1' })
  w.files.set(`${INBOX}/${NEW_MESSAGE}`, messageFile('beta', 'Fresh news'))
  await clock.advance(6000)
  expect(w.submitted).toEqual([])

  // The turn ends; two idle sweeps later Claude is told, once.
  await $.turn.complete({ answer: '', durationMs: 1, isAborted: false, turnId: 't1', reason: 'answer' })
  await clock.advance(6000)
  expect(w.submitted.length).toBe(1)
  expect(w.submitted[0].asUser).toBe(false)
  expect(w.submitted[0].text).toContain('"Fresh news" from beta')
  expect(w.submitted[0].text).toContain("wait for the user's go")
  await clock.advance(10000)
  expect(w.submitted.length).toBe(1)
})

test('with the inbox off, or a prompt being typed, nothing is handed over', async ($, on) => {
  const w = world(on)
  const clock = w.clock
  await $.session.start({ cwd: ROOT, surface: 'desktop', isInteractive: true })

  w.draft = 'half a thought'
  w.files.set(`${INBOX}/${NEW_MESSAGE}`, messageFile('beta', 'Fresh news'))
  await clock.advance(10000)
  expect(w.submitted).toEqual([])

  const pane = await $.ui.mount({ plugin: 'firepit-commands', surface: 'desktop', component: 'Pane', requestId: 'firepit-commands', props: PANE_PROPS })
  await pane.press({ key: 'mode-off' })
  w.draft = ''
  await clock.advance(10000)
  expect(w.submitted).toEqual([])

  // Switching back on hands over only what comes after.
  await pane.press({ key: 'mode-act' })
  await clock.advance(10000)
  expect(w.submitted).toEqual([])
  w.files.set(`${INBOX}/2026-10-13T11-00-00Z-from-gamma-later.md`, messageFile('gamma', 'Later'))
  await clock.advance(6000)
  expect(w.submitted.length).toBe(1)
  expect(w.submitted[0].text).toContain('act on arrivals')
  await pane.unmount()
})
