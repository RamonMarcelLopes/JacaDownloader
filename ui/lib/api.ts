export type EntryStatus = 'downloading' | 'done' | 'failed'
export type Tab = 'download' | 'history' | 'converter' | 'settings'
export type Theme = 'system' | 'light' | 'dark'

export type Entry = {
  id: string
  url: string
  platform: string
  type: 'video' | 'audio'
  quality: number
  height: number | null
  title: string
  uploader: string | null
  duration: number | null
  thumb: string | null
  outputDir: string
  cookies: string | null
  filePath: string | null
  fileSize: number | null
  createdAt: string
  status: EntryStatus
  error: string | null
  percent: number
  message: string
  fileExists: boolean
}

export type Info = {
  title: string | null
  uploader: string | null
  thumbnail: string | null
  duration: number | null
  heights: number[]
  spotify: boolean
  platform: string
}

export type Settings = {
  defaultDir: string
  imageDir: string
  defaultType: 'video' | 'audio'
  defaultQuality: number
  cookies: string
  theme: Theme
  historyDays: number
  lastTab: Tab
}

export type ToolStatus = { ready: boolean; message: string; error: string | null; version: string | null }

export async function api<T = unknown>(path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method: body === undefined ? 'GET' : 'POST',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const data = await response.json().catch(() => ({}))
  if (!response.ok) throw new Error((data as { error?: string }).error || 'Algo deu errado. Tente de novo.')
  return data as T
}

export type ConvertFormats = { sources: string[]; destinations: string[] }

// Uploads one image to the local backend, which converts it and keeps the result until it is saved.
export async function convertImage(file: File, to: string): Promise<{ id: string; size: number }> {
  const response = await fetch(`/api/convert?name=${encodeURIComponent(file.name)}&to=${encodeURIComponent(to.toLowerCase())}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/octet-stream' },
    body: file,
  })
  const data = await response.json().catch(() => ({}))
  if (!response.ok) throw new Error((data as { error?: string }).error || 'Não foi possível converter este arquivo.')
  return data as { id: string; size: number }
}

// Sends a command to the native window (minimize, maximize, close, theme).
export function hostMessage(message: string) {
  ;(window as unknown as { chrome?: { webview?: { postMessage: (m: string) => void } } }).chrome?.webview?.postMessage(message)
}

export const platformLabels: Record<string, string> = {
  youtube: 'YouTube',
  tiktok: 'TikTok',
  instagram: 'Instagram',
  x: 'X',
  spotify: 'Spotify',
  other: 'Outro',
}

export function platformLabel(platform: string) {
  return platformLabels[platform] ?? 'Outro'
}

export function formatDuration(seconds: number | null | undefined) {
  if (!seconds) return ''
  const total = Math.round(seconds)
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = String(total % 60).padStart(2, '0')
  return h ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`
}

export function formatSize(bytes: number | null | undefined) {
  if (!bytes) return ''
  const mb = bytes / 1048576
  if (mb < 1) return `${Math.max(1, Math.round(bytes / 1024))} KB`
  return `${mb.toFixed(1).replace('.', ',')} MB`
}

export function formatDate(iso: string) {
  const date = new Date(iso)
  const now = new Date()
  const yesterday = new Date(now)
  yesterday.setDate(now.getDate() - 1)
  const time = date.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' })
  if (date.toDateString() === now.toDateString()) return `Hoje, ${time}`
  if (date.toDateString() === yesterday.toDateString()) return `Ontem, ${time}`
  const day = date.toLocaleDateString('pt-BR', {
    day: 'numeric',
    month: 'short',
    ...(date.getFullYear() !== now.getFullYear() ? { year: 'numeric' as const } : {}),
  })
  return `${day}, ${time}`
}

export function formatLabel(entry: Pick<Entry, 'type' | 'height' | 'quality'>) {
  if (entry.type === 'audio') return 'MP3'
  const height = entry.height ?? entry.quality
  return height ? `MP4 ${height}p` : 'MP4'
}

export function fileNameOf(path: string | null) {
  return path ? path.split(/[\\/]/).pop() ?? path : ''
}

export async function copyText(text: string) {
  try {
    await navigator.clipboard.writeText(text)
  } catch {
    const field = document.createElement('textarea')
    field.value = text
    document.body.appendChild(field)
    field.select()
    document.execCommand('copy')
    field.remove()
  }
}
