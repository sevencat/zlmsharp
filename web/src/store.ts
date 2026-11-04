import { reactive } from 'vue'
import { call } from './api'
import type { MediaInfo, OverlayState, RecordState, VersionInfo } from './types'

export const state = reactive({
  version: null as VersionInfo | null,
  versionError: '',
  media: [] as MediaInfo[],
  mediaError: '',
  /** 录像任务（含录制中的流），与在线流一起 5s 轮询 */
  recordings: [] as RecordState[],
  /** 叠加会话（用于识别叠加输出流，未装 Extra 时为空） */
  overlays: [] as OverlayState[],
  loading: false,
  updatedAt: 0,
})

let timer: number | undefined

const message = (error: unknown) => (error instanceof Error ? error.message : String(error))

/** 拉取版本 + 在线流（5s 轮询的同一入口，手动刷新也复用） */
export async function refresh() {
  state.loading = true
  try {
    try {
      state.version = await call<VersionInfo>('version')
      state.versionError = ''
    } catch (error) {
      state.versionError = message(error)
    }

    try {
      state.media = await call<MediaInfo[]>('getMediaList')
      state.mediaError = ''
    } catch (error) {
      state.mediaError = message(error)
    }

    // 录像未启用时返回空数组，不当作错误
    try {
      state.recordings = await call<RecordState[]>('getRecordList')
    } catch {
      state.recordings = []
    }

    // 未装 Extra 时返回空数组，不当作错误
    try {
      state.overlays = await call<OverlayState[]>('getOverlay')
    } catch {
      state.overlays = []
    }

    state.updatedAt = Date.now()
  } finally {
    state.loading = false
  }
}

export function startPolling(intervalMs = 5000) {
  stopPolling()
  void refresh()
  timer = window.setInterval(() => void refresh(), intervalMs)
}

export function stopPolling() {
  if (timer !== undefined) {
    window.clearInterval(timer)
    timer = undefined
  }
}

export function streamLabel(item: { app: string; stream: string }) {
  return `${item.app}/${item.stream}`
}

export function seconds(value: number) {
  if (!value || value < 0) return '-'
  const total = Math.floor(value)
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  if (h > 0) return `${h}h${String(m).padStart(2, '0')}m`
  if (m > 0) return `${m}m${String(s).padStart(2, '0')}s`
  return `${s}s`
}
