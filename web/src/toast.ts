import { reactive } from 'vue'

export type ToastType = 'success' | 'error' | 'info'

export interface ToastItem {
  id: number
  type: ToastType
  message: string
}

let sequence = 0

/** 全局轻提示队列（App.vue 负责渲染） */
export const toasts = reactive<{ items: ToastItem[] }>({ items: [] })

export function toast(type: ToastType, message: string, timeoutMs = 3500) {
  const id = ++sequence
  toasts.items.push({ id, type, message })

  window.setTimeout(() => {
    const index = toasts.items.findIndex((item) => item.id === id)
    if (index >= 0) {
      toasts.items.splice(index, 1)
    }
  }, timeoutMs)
}

/** 从异常里取消息并弹错误提示；prefix 用于标明是哪个操作失败 */
export function toastError(error: unknown, prefix?: string) {
  const message = error instanceof Error ? error.message : String(error)
  toast('error', prefix ? `${prefix}：${message}` : message, 6000)
}
