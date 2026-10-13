import type { On } from 'claude-code'
import { expect, test } from 'claude-code/testing'

const ROOT = '/r/firepit-template'

const CONFIG = JSON.stringify({
  id: 'firepit-template',
  commands: [
    { name: 'Site', description: 'Serves the site.', type: 'shell', group: 'Run', command: 'python3', args: ['-m', 'http.server'], window: 'reuse:site', longRunning: true },
    { name: 'Clean', type: 'shell', command: 'rm', args: ['-rf', 'output'], confirm: true },
    { name: 'Ask', type: 'claude-prompt', prompt: 'Summarize.' },
  ],
})

const BAND = {
  component: 'AbovePrompt',
  props: { hasSurvey: false, isWorking: false, maxRows: 6, bodyColumns: 100, scroll: { offset: 0, bodyRows: 6 }, view: {} },
} as const

type Ran = string[][]

/** The world beneath the plugin: one project on disk, systemd answering, a store in memory. */
function world(on: On, ran: Ran, submitted: string[]) {
  const store = new Map<string, unknown>()
  on('session.root', () => ({ value: ROOT }))
  on('fs.stat', (_$, e) =>
    e.path === `${ROOT}/.firepit/config.json`
      ? { value: { kind: 'file', size: CONFIG.length, mtimeMs: 1, isLink: false } }
      : { deny: 'ENOENT' },
  )
  on('fs.read', () => ({ value: CONFIG }))
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('ui.toast', () => ({ value: undefined }))
  on('store.get', (_$, e) => ({ value: store.get(e.key) }))
  on('store.set', (_$, e) => {
    store.set(e.key, e.value)
    return { value: undefined }
  })
  on('env.get', (_$, e) => ({ value: e.name === 'PATH' ? '/usr/bin' : undefined }))
  on('process.run', (_$, e) => {
    ran.push([...e.argv])
    return { value: { exitCode: 0, stdout: '', stderr: '', isStdoutTruncated: false, isStderrTruncated: false } }
  })
  on('prompt.submit', (_$, e) => {
    submitted.push(e.text)
    return { text: e.text }
  })
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
    const pane = await $.ui.mount({ plugin: 'firepit-commands', surface, component: 'Pane', requestId: 'firepit-commands', props: { title: 'Commands', isFocused: false, bodyColumns: 80, placement: 'dock', scroll: { offset: 0, bodyRows: 40 } } })
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
  world(on, [], [])
  await $.command.run({ command: 'fire', args: 'reload' })
  const before = await $.command.run({ command: 'fire', args: 'status' })
  expect(before.text).toContain('firepit-commands 0.2.0')
  expect(before.text).toContain('trusted: no')
  const trusted = await $.command.run({ command: 'fire', args: 'trust' })
  expect(trusted.text).toContain('Trusted 2 shell commands:')
  expect(trusted.text).toContain('run site — python3 -m http.server')
  const after = await $.command.run({ command: 'fire', args: 'status' })
  expect(after.text).toContain('trusted: yes')
})
