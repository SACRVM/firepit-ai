import { describe, expect, test } from 'claude-code/testing'

import type { InboxMessage } from '../types'
import { ago, clip, deliveryPrompt, handOverPrompt, parseMessage, sweep, VIEW_LIMIT } from './inbox'

const FILE = [
  '---',
  'from: sacrvm-appkit',
  'to: vectorizer',
  'subject: sacrvm-appkit v2.32.0 — re-vendor now',
  'sentAt: 2026-10-13T18:23:10.6962447Z',
  'priority: high',
  '---',
  '',
  '**Re-vendor:** delete `kit/` and unzip it.',
  '',
].join('\r\n')

const message = (id: string, subject = id): InboxMessage => ({ id, from: 'a', subject, priority: 'normal', sentAt: null })

describe('parseMessage', () => {
  test('reads the frontmatter and keeps the body', () => {
    const { message: m, body } = parseMessage('2026-10-13T18-23-10Z-from-sacrvm-appkit-x.md', FILE)
    expect(m).toEqual({
      id: '2026-10-13T18-23-10Z-from-sacrvm-appkit-x.md',
      from: 'sacrvm-appkit',
      subject: 'sacrvm-appkit v2.32.0 — re-vendor now',
      priority: 'high',
      sentAt: '2026-10-13T18:23:10.6962447Z',
    })
    expect(body).toBe('**Re-vendor:** delete `kit/` and unzip it.')
  })

  test('a file without frontmatter is all body; the name gives subject and time', () => {
    const { message: m, body } = parseMessage('2026-09-25T08-00-17Z-from-x-hello.md', 'Just text.')
    expect(m.subject).toBe('2026-09-25T08-00-17Z-from-x-hello')
    expect(m.from).toBe('unknown')
    expect(m.priority).toBe('normal')
    expect(m.sentAt).toBe('2026-09-25T08:00:17Z')
    expect(body).toBe('Just text.')
  })

  test('quoted values lose their quotes; a colon inside a value stays', () => {
    const { message: m } = parseMessage('m.md', '---\nsubject: "Re: the plan"\nfrom: \'b\'\n---\nx')
    expect(m.subject).toBe('Re: the plan')
    expect(m.from).toBe('b')
  })
})

test('ago reads seven fractional digits and steps from minutes to dates', () => {
  const now = Date.parse('2026-10-13T20:00:00Z')
  expect(ago('2026-10-13T19:59:30.1234567Z', now)).toBe('just now')
  expect(ago('2026-10-13T19:55:00Z', now)).toBe('5 min ago')
  expect(ago('2026-10-13T17:00:00Z', now)).toBe('3 h ago')
  expect(ago('2026-10-11T20:00:00Z', now)).toBe('2 d ago')
  expect(ago('2026-09-25T08:00:00Z', now)).toBe('25 Sep')
  expect(ago(null, now)).toBe('')
  expect(ago('not a date', now)).toBe('')
})

describe('sweep', () => {
  const fresh = [message('a')]

  test('hands over after two idle sweeps, not one', () => {
    const first = sweep({ mode: 'tell', isIdle: true, idleSweeps: 0, fresh })
    expect(first.deliver).toEqual([])
    const second = sweep({ mode: 'tell', isIdle: true, idleSweeps: first.idleSweeps, fresh })
    expect(second.deliver).toEqual(fresh)
  })

  test('a busy sweep starts the count over', () => {
    expect(sweep({ mode: 'act', isIdle: false, idleSweeps: 1, fresh })).toEqual({ idleSweeps: 0, deliver: [] })
  })

  test('off never hands over; nothing fresh means nothing to hand over', () => {
    expect(sweep({ mode: 'off', isIdle: true, idleSweeps: 5, fresh }).deliver).toEqual([])
    expect(sweep({ mode: 'tell', isIdle: true, idleSweeps: 5, fresh: [] }).deliver).toEqual([])
  })
})

describe('prompts', () => {
  test('tell asks for a summary and a wait', () => {
    const text = deliveryPrompt('tell', [message('a', 'Hello')])
    expect(text).toContain('1 new message arrived')
    expect(text).toContain('"Hello" from a')
    expect(text).toContain("wait for the user's go")
  })

  test('act asks for the work, fenced, and lists at most five', () => {
    const many = ['1', '2', '3', '4', '5', '6', '7'].map(id => message(id))
    const text = deliveryPrompt('act', many)
    expect(text).toContain('7 new messages arrived')
    expect(text).toContain('and 2 more')
    expect(text).toContain('firepit_inbox_complete')
    expect(text).toContain("don't send this onward")
  })

  test('a hand-over names the message, or the whole inbox', () => {
    expect(handOverPrompt(message('m.md', 'Hi'))).toContain('"Hi" from a (id m.md)')
    expect(handOverPrompt(null)).toContain('Work through the Firepit inbox')
  })
})

test('clip keeps newlines and tabs, drops other control characters, cuts long text', () => {
  expect(clip('a\r\nb\tc\u001b[0m')).toEqual({ text: 'a\nb\tc[0m', isCut: false })
  const long = clip('x'.repeat(VIEW_LIMIT + 5))
  expect(long.isCut).toBe(true)
  expect(long.text.length).toBe(VIEW_LIMIT)
})
