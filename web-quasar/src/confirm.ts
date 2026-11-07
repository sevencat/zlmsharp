import { Dialog } from 'quasar'

/** 危险操作二次确认：返回 true 表示用户点了确定 */
export function confirmAction(message: string, title = '确认操作'): Promise<boolean> {
  return new Promise((resolve) => {
    Dialog.create({
      title,
      message,
      persistent: true,
      ok: { label: '确定', color: 'negative', unelevated: true },
      cancel: { label: '取消', flat: true, color: 'grey-7' },
    })
      .onOk(() => resolve(true))
      .onCancel(() => resolve(false))
      .onDismiss(() => resolve(false))
  })
}
