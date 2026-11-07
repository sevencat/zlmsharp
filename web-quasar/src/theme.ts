import { Dark } from 'quasar'
import { ref } from 'vue'

const KEY = 'zlmsharp.theme'

/**
 * 明暗主题：首次访问跟随系统偏好，之后以用户手动切换的结果为准。
 * Quasar 的 Dark 插件只负责给 body 加 body--dark 类，实际配色在 styles/app.scss 里。
 */
function initialValue() {
  const stored = localStorage.getItem(KEY)
  if (stored === 'dark' || stored === 'light') return stored === 'dark'
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches === true
}

export const isDark = ref(initialValue())

export function initTheme() {
  Dark.set(isDark.value)
}

export function toggleTheme() {
  isDark.value = !isDark.value
  localStorage.setItem(KEY, isDark.value ? 'dark' : 'light')
  Dark.set(isDark.value)
}
