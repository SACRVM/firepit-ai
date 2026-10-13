import { describe, expect, test } from 'claude-code/testing'

import {
  findCommands,
  inlineArgv,
  launchArgv,
  parseActiveUnits,
  parseJsonc,
  phraseOf,
  readCommands,
  resolveCwd,
  sections,
  slug,
  trustMaterial,
  unitFor,
  windowsWarning,
} from './logic'

const CONFIG = `{
  // a comment with "quotes" and a // inside
  "id": "firepit-template",
  "commands": [
    { "name": "Site", "type": "shell", "group": "Run", "command": "python3",
      "args": ["-m", "http.server"], "window": "reuse:site", "longRunning": true },
    { "name": "Build (fails)", "type": "shell", "command": "bash", "args": ["scripts/fail.sh"],
      "keepOpenOnError": true },
    { "name": "Env", "type": "shell", "command": "bash", "args": ["env.sh"], "cwd": "scripts",
      "env": { "A": "1", "GONE": null }, "window": "inline" },
    { "name": "Off", "type": "shell", "command": "true", "disabled": true },
    { "name": "Win", "type": "shell", "command": "powershell", "args": ["-c", "x"], "elevated": true },
    { "name": "Ask", "type": "claude-prompt", "prompt": "hi" },
    { "name": "Page", "type": "url", "url": "http://127.0.0.1:8765/" }, /* trailing comma next */
  ],
}`

const CTX = { root: '/r/firepit-template', projectId: 'firepit-template', desktopEnv: { PATH: '/usr/bin' }, nonce: 'n1' }

describe('config', () => {
  test('JSONC with comments and trailing commas parses', () => {
    const parsed = parseJsonc(CONFIG) as { id: string; commands: unknown[] }
    expect(parsed.id).toBe('firepit-template')
    expect(parsed.commands.length).toBe(7)
  })

  test('a // inside a string survives', () => {
    expect(parseJsonc('{ "url": "https://example.com/a" }')).toEqual({ url: 'https://example.com/a' })
  })

  test('commands are normalised, disabled ones dropped, Windows ones flagged', () => {
    const { id, commands, problems } = readCommands(parseJsonc(CONFIG))
    expect(id).toBe('firepit-template')
    expect(commands.map(c => c.name)).toEqual(['Site', 'Build (fails)', 'Env', 'Win', 'Ask', 'Page'])
    expect(commands[0].key).toBe('site')
    expect(commands[1].key).toBe('build-fails')
    expect(commands[2].env).toEqual({ A: '1', GONE: null })
    expect(commands[3].warning).toBe('powershell is a Windows program')
    expect(problems).toEqual(['"Win": elevated is ignored on Linux'])
  })

  test('Windows paths and executables are caught', () => {
    expect(windowsWarning('D:\\dev\\Unity.exe', [], null)).toBe('runs a Windows executable or script')
    expect(windowsWarning('tool', ['C:/x'], null)).toBe('uses a Windows drive path')
    expect(windowsWarning('python3', ['-m', 'http.server'], 'site')).toBe(null)
  })
})

describe('launching', () => {
  const { commands } = readCommands(parseJsonc(CONFIG))
  const [site, fails, env] = commands

  test('a long-running command runs bare under its reuse unit', () => {
    const argv = launchArgv(site, CTX)
    expect(argv.slice(0, 5)).toEqual(['systemd-run', '--user', '--collect', '--quiet', '--unit=firepit-firepit-template-site'])
    expect(argv).toContain('--working-directory=/r/firepit-template')
    expect(argv).toContain('--setenv=PATH=/usr/bin')
    expect(argv.slice(argv.indexOf('--') + 1)).toEqual(['/usr/bin/env', 'python3', '-m', 'http.server'])
  })

  test('a windowed command opens Konsole and keeps it open on error', () => {
    const argv = launchArgv(fails, CTX)
    expect(argv).toContain('--unit=firepit-firepit-template-build-fails-n1')
    const tail = argv.slice(argv.indexOf('--') + 1)
    expect(tail.slice(0, 6)).toEqual(['konsole', '--workdir', '/r/firepit-template', '-e', '/usr/bin/env', '/bin/bash'])
    expect(tail.slice(-3)).toEqual(['firepit', 'bash', 'scripts/fail.sh'])
  })

  test('inline unsets null variables, cwd is joined to the root', () => {
    expect(inlineArgv(env)).toEqual(['/usr/bin/env', '-u', 'GONE', 'bash', 'env.sh'])
    expect(resolveCwd('/r/p/', 'scripts')).toBe('/r/p/scripts')
    expect(resolveCwd('/r/p', null)).toBe('/r/p')
    expect(resolveCwd('/r/p', '/abs')).toBe('/abs')
  })

  test('unit names are stable per reuse id, unique per new click', () => {
    expect(unitFor(site, CTX)).toBe('firepit-firepit-template-site')
    expect(unitFor(fails, { ...CTX, nonce: 'a' })).not.toBe(unitFor(fails, { ...CTX, nonce: 'b' }))
  })
})

describe('the rest', () => {
  test('active units come out of a plain listing', () => {
    const listing = [
      'firepit-x-site.service loaded active running Firepit: x / Site',
      'firepit-x-old.service loaded inactive dead Firepit: x / Old',
      'firepit-x-boot.service loaded activating start Firepit: x / Boot',
    ].join('\n')
    expect(parseActiveUnits(listing)).toEqual(['firepit-x-site', 'firepit-x-boot'])
  })

  test('trust covers what runs, not the label of a prompt', () => {
    const a = readCommands(parseJsonc(CONFIG)).commands
    const changedPrompt = a.map(c => (c.type === 'claude-prompt' ? { ...c, prompt: 'other' } : c))
    const changedArgs = a.map(c => (c.key === 'site' ? { ...c, args: ['-c', 'rm -rf ~'] } : c))
    expect(trustMaterial(changedPrompt)).toBe(trustMaterial(a))
    expect(trustMaterial(changedArgs)).not.toBe(trustMaterial(a))
  })

  test('commands are found by phrase, name, key or prefix; ambiguity is reported', () => {
    const { commands } = readCommands(parseJsonc(CONFIG))
    const names = (q: string) => findCommands(commands, q).map(phraseOf)
    expect(names('site')).toEqual(['Run Site'])
    expect(names('run site')).toEqual(['Run Site'])
    expect(names('run:site')).toEqual(['Run Site'])
    expect(names('Build (fails)')).toEqual(['Build (fails)'])
    expect(names('pa')).toEqual(['Page'])
    expect(names('nope')).toEqual([])
    expect(names('e')).toEqual(['Env'])
    expect(slug('  Hello, World!  ')).toBe('hello-world')
  })
})

describe('naming and layout', () => {
  const NAMED = `{
    "commands": [
      { "name": "site", "group": "Run", "type": "shell", "command": "python3", "description": "Serves site/." },
      { "name": "status", "group": "Git", "type": "shell", "command": "git", "args": ["status"], "window": "inline" },
      { "name": "repo", "group": "Git", "type": "url", "url": "https://example.com", "bar": false },
      { "name": "site", "group": "Open", "type": "url", "url": "http://127.0.0.1:8765/" },
      { "name": "status", "group": "Git", "type": "shell", "command": "true" }
    ],
    "quickLinks": [ { "name": "Docs", "url": "https://docs.example.com" } ]
  }`

  test('keys come from group and name, so short names may repeat across groups', () => {
    const { commands, problems } = readCommands(parseJsonc(NAMED))
    expect(commands.map(c => c.key)).toEqual(['run-site', 'git-status', 'git-repo', 'open-site', 'git-status-2', 'links-docs'])
    expect(problems).toEqual(['"Git status" is defined twice; names must be unique within a group'])
  })

  test('description, bar flag and legacy quick links are read', () => {
    const { commands } = readCommands(parseJsonc(NAMED))
    expect(commands[0].description).toBe('Serves site/.')
    expect(commands[2].inBar).toBe(false)
    expect(commands[5]).toMatchObject({ group: 'Links', name: 'Docs', type: 'url', url: 'https://docs.example.com' })
  })

  test('a name shared by two groups is ambiguous until the group is given', () => {
    const { commands } = readCommands(parseJsonc(NAMED))
    expect(findCommands(commands, 'site').map(phraseOf)).toEqual(['Run site', 'Open site'])
    expect(findCommands(commands, 'open site').map(phraseOf)).toEqual(['Open site'])
    expect(findCommands(commands, 'git/repo').map(phraseOf)).toEqual(['Git repo'])
  })

  test('sections keep the order of first appearance', () => {
    const { commands } = readCommands(parseJsonc(NAMED))
    expect(sections(commands).map(s => [s.group, s.items.map(c => c.name)])).toEqual([
      ['Run', ['site']],
      ['Git', ['status', 'repo', 'status']],
      ['Open', ['site']],
      ['Links', ['Docs']],
    ])
  })
})
