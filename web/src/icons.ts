// 图标全部来自 @mdi/js 的 SVG path（纯数据，可 tree-shake），运行时不加载任何字体文件。
// 键名用 mdi 的短名（Buefy 内部默认图标即为 'arrow-up' / 'close' 之类）。
import {
  mdiAccountMultiple,
  mdiAlert,
  mdiAlertCircle,
  mdiApi,
  mdiArrowDown,
  mdiArrowLeft,
  mdiArrowRight,
  mdiArrowUp,
  mdiBroadcast,
  mdiCalendar,
  mdiChartLine,
  mdiCheck,
  mdiCheckCircle,
  mdiChevronDown,
  mdiChevronLeft,
  mdiChevronRight,
  mdiChevronUp,
  mdiClockOutline,
  mdiClose,
  mdiCloseCircle,
  mdiCog,
  mdiContentCopy,
  mdiDelete,
  mdiDotsHorizontal,
  mdiDownload,
  mdiEye,
  mdiEyeOff,
  mdiFilmstrip,
  mdiFlash,
  mdiHome,
  mdiInformationOutline,
  mdiLanConnect,
  mdiMagnify,
  mdiMenuDown,
  mdiPlay,
  mdiPlus,
  mdiRecord,
  mdiRefresh,
  mdiRobot,
  mdiServer,
  mdiStop,
  mdiTune,
  mdiUpload,
  mdiVideo,
} from '@mdi/js'

const icons: Record<string, string> = {
  'account-multiple': mdiAccountMultiple,
  alert: mdiAlert,
  'alert-circle': mdiAlertCircle,
  api: mdiApi,
  'arrow-down': mdiArrowDown,
  'arrow-left': mdiArrowLeft,
  'arrow-right': mdiArrowRight,
  'arrow-up': mdiArrowUp,
  broadcast: mdiBroadcast,
  calendar: mdiCalendar,
  'chart-line': mdiChartLine,
  check: mdiCheck,
  'check-circle': mdiCheckCircle,
  'chevron-down': mdiChevronDown,
  'chevron-left': mdiChevronLeft,
  'chevron-right': mdiChevronRight,
  'chevron-up': mdiChevronUp,
  'clock-outline': mdiClockOutline,
  close: mdiClose,
  'close-circle': mdiCloseCircle,
  cog: mdiCog,
  'content-copy': mdiContentCopy,
  delete: mdiDelete,
  'dots-horizontal': mdiDotsHorizontal,
  download: mdiDownload,
  eye: mdiEye,
  'eye-off': mdiEyeOff,
  filmstrip: mdiFilmstrip,
  flash: mdiFlash,
  home: mdiHome,
  'information-outline': mdiInformationOutline,
  'lan-connect': mdiLanConnect,
  magnify: mdiMagnify,
  'menu-down': mdiMenuDown,
  play: mdiPlay,
  plus: mdiPlus,
  record: mdiRecord,
  refresh: mdiRefresh,
  robot: mdiRobot,
  server: mdiServer,
  stop: mdiStop,
  tune: mdiTune,
  upload: mdiUpload,
  video: mdiVideo,
}

/**
 * 归一化图标名，兼容几种输入：
 * - `'arrow-up'`、`'mdi-arrow-up'`、`'fa-close'`、`'fas-close'`
 * - Buefy 通过 `defaultIconComponent` 传给自定义组件的 `[pack, name]` 数组，如 `['mdi', 'mdi-play']`
 */
export function normalizeIconName(value: unknown): string {
  if (Array.isArray(value)) {
    for (let index = value.length - 1; index >= 0; index -= 1) {
      const name = normalizeIconName(value[index])
      if (name) return name
    }
    return ''
  }

  if (typeof value !== 'string') return ''

  const raw = value.trim()
  if (!raw) return ''

  const name = raw.replace(/^(mdi|fa[srlbd]?)-/, '').toLowerCase()

  // 'mdi-24px' 之类是尺寸类名而不是图标名
  if (!name || /^\d+px$/.test(name)) return ''
  return name
}

/** 取图标对应的 SVG path；未登记时返回空串（调用方通常画个占位圈） */
export function resolveIcon(value: unknown): string {
  const name = normalizeIconName(value)
  return name ? icons[name] ?? '' : ''
}
