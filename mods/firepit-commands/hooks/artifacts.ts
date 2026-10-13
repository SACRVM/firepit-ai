// Pure logic for the artifact links: no `$`, no I/O. Unit-tested in artifacts.test.ts.

import type { Artifact, ArtifactKind } from '../types'

// The same classes as Firepit.Core's ArtifactResolver.
const KINDS: [ArtifactKind, string[]][] = [
  ['image', ['png', 'jpg', 'jpeg', 'gif', 'svg', 'webp', 'bmp', 'ico']],
  ['markdown', ['md', 'markdown']],
  ['text', ['txt', 'log', 'json', 'jsonc', 'yaml', 'yml', 'csv', 'xml', 'ini', 'diff', 'patch']],
  ['document', ['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'odt', 'rtf']],
  ['executable', ['exe', 'bat', 'cmd', 'ps1', 'msi', 'com', 'scr', 'lnk']],
  ['archive', ['zip', '7z', 'rar', 'tar', 'gz', 'tgz', 'bz2']],
]

export function isWindowsPath(path: string): boolean {
  return /^[A-Za-z]:[\\/]/.test(path) || path.startsWith('\\\\')
}

/** The last segment of either kind of path. */
export function fileName(path: string): string {
  return path.split(/[\\/]/).filter(Boolean).pop() ?? path
}

export function kindOf(path: string): ArtifactKind {
  const name = fileName(path)
  const dot = name.lastIndexOf('.')
  if (dot <= 0) return 'other'
  const ext = name.slice(dot + 1).toLowerCase()
  return KINDS.find(([, exts]) => exts.includes(ext))?.[0] ?? 'other'
}

/** Relative to the root, or absolute as written; a Windows path cannot be resolved here. */
export function absoluteOf(root: string, path: string): string {
  if (path.startsWith('/') || isWindowsPath(path)) return path
  return `${root.replace(/\/+$/, '')}/${path.replace(/^\.\//, '')}`
}

/** Markdown and text open in the viewer; everything else in its own program. */
export function isViewable(kind: ArtifactKind): boolean {
  return kind === 'markdown' || kind === 'text'
}

/** `file://` with each segment escaped, as the file manager's ShowItems wants it. */
export function fileUri(absolute: string): string {
  return `file://${absolute.split('/').map(encodeURIComponent).join('/')}`
}

/** `.firepit/artifacts.json` → links, newest first; `exists` is the caller's to fill in. */
export function readArtifacts(json: unknown, root: string): Artifact[] {
  const list = (typeof json === 'object' && json !== null ? (json as Record<string, unknown>).artifacts : null) ?? []
  if (!Array.isArray(list)) return []
  const out: Artifact[] = []
  for (const item of list) {
    if (typeof item !== 'object' || item === null) continue
    const a = item as Record<string, unknown>
    if (typeof a.path !== 'string' || a.path === '') continue
    out.push({
      path: a.path,
      absolute: absoluteOf(root, a.path),
      label: typeof a.label === 'string' && a.label !== '' ? a.label : fileName(a.path),
      note: typeof a.note === 'string' && a.note !== '' ? a.note : null,
      kind: kindOf(a.path),
      addedAt: typeof a.addedAtUtc === 'string' ? a.addedAtUtc : null,
      exists: false,
    })
  }
  // ISO times sort as text; entries without one go last.
  return out.sort((x, y) => (y.addedAt ?? '').localeCompare(x.addedAt ?? ''))
}

/** The viewer shows this much; a longer file says it was cut. */
export const VIEW_LIMIT = 60000

/** Drops the control characters a drawing refuses; newline and tab stay. */
export function printable(text: string): string {
  return text.replace(/\r\n?/g, '\n').replace(/[\u0000-\u0008\u000b-\u001f\u007f]/g, '')
}

export function clip(text: string): { text: string; isCut: boolean } {
  const clean = printable(text)
  return clean.length <= VIEW_LIMIT ? { text: clean, isCut: false } : { text: clean.slice(0, VIEW_LIMIT), isCut: true }
}
