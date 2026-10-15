// Pure logic for the inbox: no `$`, no I/O. Unit-tested in inbox.test.ts.

import type { InboxMessage, InboxMode } from '../types'

/** `2026-10-13T18-23-10Z-from-…` → `2026-10-13T18:23:10Z`, the time a file name carries. */
function sentAtFromId(id: string): string | null {
  const m = /^(\d{4}-\d{2}-\d{2})T(\d{2})-(\d{2})-(\d{2})Z/.exec(id)
  return m === null ? null : `${m[1]}T${m[2]}:${m[3]}:${m[4]}Z`
}

/** An inbox file's frontmatter and body; a file without frontmatter is all body. */
export function parseMessage(id: string, text: string): { message: InboxMessage; body: string } {
  const normalised = text.replace(/\r\n/g, '\n')
  const fields: Record<string, string> = {}
  let body = normalised
  const head = /^---\n([\s\S]*?)\n---(?:\n|$)/.exec(normalised)
  if (head !== null) {
    body = normalised.slice(head[0].length)
    for (const line of head[1].split('\n')) {
      const colon = line.indexOf(':')
      if (colon <= 0) continue
      let value = line.slice(colon + 1).trim()
      if (value.length >= 2 && (value[0] === '"' || value[0] === "'") && value.endsWith(value[0])) {
        value = value.slice(1, -1)
      }
      fields[line.slice(0, colon).trim()] = value
    }
  }
  return {
    message: {
      id,
      from: fields.from || 'unknown',
      subject: fields.subject || id.replace(/\.md$/i, ''),
      priority: (fields.priority || 'normal').toLowerCase(),
      sentAt: fields.sentAt || fields.date || sentAtFromId(id),
    },
    body: body.replace(/^\n+/, '').trimEnd(),
  }
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** "just now", "5 min ago", "3 h ago", "2 d ago", else "25 Sep". */
export function ago(iso: string | null, now: number): string {
  if (iso === null) return ''
  // .NET writes seven fractional digits; not every engine parses more than three.
  const time = Date.parse(iso.replace(/(\.\d{3})\d+/, '$1'))
  if (Number.isNaN(time)) return ''
  const seconds = Math.max(0, Math.round((now - time) / 1000))
  if (seconds < 60) return 'just now'
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} h ago`
  const days = Math.round(hours / 24)
  if (days < 14) return `${days} d ago`
  const date = new Date(time)
  return `${date.getUTCDate()} ${MONTHS[date.getUTCMonth()]}`
}

/** Two consecutive idle sweeps: a lull between two tool calls is not idle. */
export const IDLE_SWEEPS_BEFORE_DELIVERY = 2

/**
 * One sweep of the delivery decision. Idle sweeps count up while the session
 * waits for the person with an empty prompt box; anything else resets them.
 * Messages are handed over only after enough idle sweeps, and never with the
 * mode off.
 */
export function sweep(input: {
  mode: InboxMode
  isIdle: boolean
  idleSweeps: number
  fresh: InboxMessage[]
}): { idleSweeps: number; deliver: InboxMessage[] } {
  if (!input.isIdle) return { idleSweeps: 0, deliver: [] }
  const idleSweeps = input.idleSweeps + 1
  if (input.mode === 'off' || input.fresh.length === 0 || idleSweeps < IDLE_SWEEPS_BEFORE_DELIVERY) {
    return { idleSweeps, deliver: [] }
  }
  return { idleSweeps: 0, deliver: input.fresh }
}

function listed(messages: InboxMessage[]): string {
  const shown = messages.slice(0, 5).map(m => `"${m.subject}" from ${m.from}`)
  const more = messages.length - shown.length
  return more > 0 ? `${shown.join('; ')}; and ${more} more` : shown.join('; ')
}

/**
 * What the session is told when messages arrive. It reaches Claude under the
 * plugin's name, not as the person's words: `tell` asks for a summary and a
 * wait, `act` for the work, fenced the way the Firepit shell fenced it.
 */
export function deliveryPrompt(mode: Exclude<InboxMode, 'off'>, messages: InboxMessage[]): string {
  const one = messages.length === 1
  const head = `Firepit inbox: ${one ? '1 new message' : `${messages.length} new messages`} arrived in this project — ${listed(messages)}.`
  if (mode === 'tell') {
    return (
      `${head} Read ${one ? 'it' : 'them'} with the firepit_inbox_list MCP tool and summarise what ` +
      `${one ? 'it asks' : 'each one asks'} for, then stop and wait for the user's go before acting on anything.`
    )
  }
  return (
    `${head} The user set this project's inbox to act on arrivals: read ${one ? 'it' : 'them'} with the ` +
    `firepit_inbox_list MCP tool, do what ${one ? 'it asks' : 'they ask'}, then mark each done with ` +
    `firepit_inbox_complete (id = the entry's id). Ask the user first before anything irreversible — ` +
    `deleting or overwriting files, force-pushing, cutting a release, or sending anything outside this ` +
    `machine. Report back what you did, and don't send this onward to other projects.`
  )
}

/** What a press on "Claude" sends, as the person's own request. */
export function handOverPrompt(message: InboxMessage | null): string {
  if (message === null) {
    return (
      'Work through the Firepit inbox: read the pending messages with firepit_inbox_list, act on each, ' +
      'and mark each done with firepit_inbox_complete.'
    )
  }
  return (
    `Handle the Firepit inbox message "${message.subject}" from ${message.from} (id ${message.id}): ` +
    'read it with firepit_inbox_list, act on it, then mark it done with firepit_inbox_complete.'
  )
}

/** Same list, same order, same headers: whether a write would change anything. */
export function sameMessages(a: InboxMessage[], b: InboxMessage[]): boolean {
  return JSON.stringify(a) === JSON.stringify(b)
}

export const MODES: { mode: InboxMode; label: string; explains: string }[] = [
  { mode: 'off', label: 'off', explains: 'New messages only show up here.' },
  { mode: 'tell', label: 'tell', explains: 'Claude summarises new messages and waits for your go.' },
  { mode: 'act', label: 'act', explains: 'Claude handles new messages and reports back.' },
]

export function isMode(value: unknown): value is InboxMode {
  return value === 'off' || value === 'tell' || value === 'act'
}

/** The viewer shows this much; a longer message says it was cut. */
export const VIEW_LIMIT = 60000

/** Drops the control characters a drawing refuses; newline and tab stay. */
export function printable(text: string): string {
  return text.replace(/\r\n?/g, '\n').replace(/[\u0000-\u0008\u000b-\u001f\u007f]/g, '')
}

export function clip(text: string): { text: string; isCut: boolean } {
  const clean = printable(text)
  return clean.length <= VIEW_LIMIT ? { text: clean, isCut: false } : { text: clean.slice(0, VIEW_LIMIT), isCut: true }
}
