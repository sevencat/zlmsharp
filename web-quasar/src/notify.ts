import { Notify } from 'quasar'

export type ToastType = 'success' | 'error' | 'info'

const TYPE_MAP: Record<ToastType, 'positive' | 'negative' | 'info'> = {
  success: 'positive',
  error: 'negative',
  info: 'info',
}

/** 全局轻提示（Quasar Notify），语义与 web/（Buefy 版）里的 toast 保持一致 */
export function toast(type: ToastType, message: string, timeoutMs = 3500) {
  Notify.create({
    type: TYPE_MAP[type],
    message,
    timeout: timeoutMs,
    position: 'top-right',
    progress: true,
    actions: [{ icon: 'close', color: 'white', round: true, dense: true }],
  })
}

/** 从异常里取消息并弹错误提示；prefix 用于标明是哪个操作失败 */
export function toastError(error: unknown, prefix?: string) {
  const message = error instanceof Error ? error.message : String(error)
  toast('error', prefix ? `${prefix}：${message}` : message, 6000)
}
