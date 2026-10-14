import { describe, expect, test } from 'claude-code/testing'

import { absoluteOf, clip, fileUri, isViewable, kindOf, readArtifacts, VIEW_LIMIT } from './artifacts'

const ROOT = '/r/the-fishbowl'

describe('kindOf', () => {
  test('classes by extension, as Firepit.Core does', () => {
    expect(kindOf('docs/plan.md')).toBe('markdown')
    expect(kindOf('out/BUILD.LOG')).toBe('text')
    expect(kindOf('shot.PNG')).toBe('image')
    expect(kindOf('C:\\tmp\\remap-login.sql')).toBe('other')
    expect(kindOf('setup.exe')).toBe('executable')
    expect(kindOf('Makefile')).toBe('other')
    expect(kindOf('.gitignore')).toBe('other')
  })

  test('only markdown and text open in the viewer', () => {
    expect(isViewable('markdown')).toBe(true)
    expect(isViewable('text')).toBe(true)
    expect(isViewable('image')).toBe(false)
  })
})

test('absoluteOf joins relative paths and leaves absolute and Windows ones', () => {
  expect(absoluteOf(ROOT, 'docs/plan.md')).toBe('/r/the-fishbowl/docs/plan.md')
  expect(absoluteOf(`${ROOT}/`, './docs/plan.md')).toBe('/r/the-fishbowl/docs/plan.md')
  expect(absoluteOf(ROOT, '/tmp/x.log')).toBe('/tmp/x.log')
  expect(absoluteOf(ROOT, 'C:\\Users\\x\\a.sql')).toBe('C:\\Users\\x\\a.sql')
})

test('readArtifacts: newest first, labels default to the file name, junk skipped', () => {
  const links = readArtifacts(
    {
      version: 1,
      artifacts: [
        { path: 'docs/old.md', label: 'Old', addedAtUtc: '2026-08-23T23:57:02Z' },
        { path: 'C:\\Users\\x\\remap-login.sql', note: 'Prod fix', addedAtUtc: '2026-09-25T10:41:49Z' },
        { label: 'no path' },
        'junk',
        { path: 'report.md' },
      ],
    },
    ROOT,
  )
  expect(links.map(a => a.label)).toEqual(['remap-login.sql', 'Old', 'report.md'])
  expect(links[0]).toEqual({
    path: 'C:\\Users\\x\\remap-login.sql',
    absolute: 'C:\\Users\\x\\remap-login.sql',
    label: 'remap-login.sql',
    note: 'Prod fix',
    kind: 'other',
    addedAt: '2026-09-25T10:41:49Z',
    exists: false,
  })
  expect(readArtifacts(null, ROOT)).toEqual([])
  expect(readArtifacts({ artifacts: 'x' }, ROOT)).toEqual([])
})

test('fileUri escapes each segment, commas included', () => {
  expect(fileUri('/home/a b/x,y#1.md')).toBe('file:///home/a%20b/x%2Cy%231.md')
})

test('clip keeps newlines and tabs, drops other control characters, cuts long text', () => {
  expect(clip('a\r\nb\tc\u001b[0m')).toEqual({ text: 'a\nb\tc[0m', isCut: false })
  const long = clip('x'.repeat(VIEW_LIMIT + 5))
  expect(long.isCut).toBe(true)
  expect(long.text.length).toBe(VIEW_LIMIT)
})
