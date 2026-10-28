import { reactive } from 'vue'

const SECRET_KEY = 'zlmsharp.secret'

/** 全局鉴权状态：secret 存在 localStorage，回环访问时后端免校验 */
export const auth = reactive({ secret: localStorage.getItem(SECRET_KEY) ?? '' })

export function saveSecret(value: string) {
  auth.secret = value.trim()
  localStorage.setItem(SECRET_KEY, auth.secret)
}

export type LogLevel = 'ok' | 'error' | 'info'

export interface LogEntry {
  id: number
  at: string
  level: LogLevel
  title: string
  detail?: string
}

const MAX_ENTRIES = 200
let sequence = 0

export const logs = reactive<{ entries: LogEntry[] }>({ entries: [] })

export function log(level: LogLevel, title: string, detail?: unknown) {
  let text: string | undefined
  if (detail !== undefined && detail !== null) {
    text = typeof detail === 'string' ? detail : JSON.stringify(detail, null, 2)
  }

  logs.entries.unshift({
    id: ++sequence,
    at: new Date().toLocaleTimeString(),
    level,
    title,
    detail: text,
  })

  if (logs.entries.length > MAX_ENTRIES) {
    logs.entries.length = MAX_ENTRIES
  }
}

export function clearLogs() {
  logs.entries.splice(0)
}

export class ApiError extends Error {
  readonly code?: number

  constructor(message: string, code?: number) {
    super(message)
    this.name = 'ApiError'
    this.code = code
  }
}

export function apiUrl(name: string, params: Record<string, unknown> = {}): string {
  const query = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === '') continue
    query.set(key, String(value))
  }

  // 后端只从 query 读 secret（见 ApiEndpoints.AuthorizeAsync），不支持自定义 header
  if (auth.secret) {
    query.set('secret', auth.secret)
  }

  const search = query.toString()
  return `/index/api/${name}${search ? `?${search}` : ''}`
}

/** 调用 /index/api/* 并拆出 data；code 非 0 时抛 ApiError */
export async function call<T = any>(name: string, params: Record<string, unknown> = {}): Promise<T> {
  const url = apiUrl(name, params)
  const started = performance.now()

  let response: Response
  try {
    response = await fetch(url, { cache: 'no-store' })
  } catch (error) {
    const message = `${name} 请求失败：${String(error)}`
    log('error', message)
    throw new ApiError(message)
  }

  const text = await response.text()
  const elapsed = Math.round(performance.now() - started)

  let body: any
  try {
    body = JSON.parse(text)
  } catch {
    const message = `${name} 返回非 JSON（HTTP ${response.status}）：${text.slice(0, 160)}`
    log('error', message)
    throw new ApiError(message, response.status)
  }

  if (typeof body?.code === 'number' && body.code !== 0) {
    const message = `${name} 失败：${body.msg ?? `code ${body.code}`}`
    log('error', message)
    throw new ApiError(message, body.code)
  }

  if (!response.ok) {
    const message = `${name} 失败：HTTP ${response.status}`
    log('error', message)
    throw new ApiError(message, response.status)
  }

  log('ok', `${name} (${elapsed}ms)`)
  return (body?.data ?? body) as T
}

/** HTTP-FLV 拉流地址（同源，交给 mpegts.js 播放） */
export const flvUrl = (app: string, stream: string) =>
  `/${encodeURIComponent(app)}/${encodeURIComponent(stream)}.live.flv`

/** WS-FLV 拉流地址（需要更低延迟时可换用） */
export const wsFlvUrl = (app: string, stream: string) => {
  const scheme = location.protocol === 'https:' ? 'wss' : 'ws'
  return `${scheme}://${location.host}/${encodeURIComponent(app)}/${encodeURIComponent(stream)}.live.flv`
}
