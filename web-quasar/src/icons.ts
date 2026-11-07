// 追加到 Quasar svg-mdi-v7 图标集之上的自定义图标（值均为 @mdi/js 的 SVG path）。
// 因为 Quasar 的 iconSet 只给内部组件用，模板里写的图标名要靠 iconMapFn 映射成 path，
// 所以这里同时导出 customIcons 与 iconMapFn，两个入口共用同一张表。
import {
  mdiAccountMultiple,
  mdiAlert,
  mdiAlertCircleOutline,
  mdiApi,
  mdiArrowDown,
  mdiArrowLeft,
  mdiArrowUp,
  mdiBroadcast,
  mdiChartLine,
  mdiCheck,
  mdiCheckCircleOutline,
  mdiChevronDown,
  mdiChevronUp,
  mdiClose,
  mdiClockOutline,
  mdiCog,
  mdiConsoleLine,
  mdiContentCopy,
  mdiDelete,
  mdiDotsHorizontal,
  mdiDownload,
  mdiEye,
  mdiEyeOff,
  mdiFilmstrip,
  mdiFlash,
  mdiFolder,
  mdiInformationOutline,
  mdiLanConnect,
  mdiMagnify,
  mdiMenu,
  mdiPlay,
  mdiPlus,
  mdiRecord,
  mdiRefresh,
  mdiRobot,
  mdiServer,
  mdiStop,
  mdiThemeLightDark,
  mdiVideo,
  mdiWeatherNight,
  mdiWhiteBalanceSunny,
} from '@mdi/js'

export const customIcons: Record<string, string> = {
  'account-multiple': mdiAccountMultiple,
  alert: mdiAlert,
  'alert-circle': mdiAlertCircleOutline,
  api: mdiApi,
  'arrow-down': mdiArrowDown,
  'arrow-left': mdiArrowLeft,
  'arrow-up': mdiArrowUp,
  broadcast: mdiBroadcast,
  'chart-line': mdiChartLine,
  check: mdiCheck,
  'check-circle': mdiCheckCircleOutline,
  'chevron-down': mdiChevronDown,
  'chevron-up': mdiChevronUp,
  close: mdiClose,
  'clock-outline': mdiClockOutline,
  cog: mdiCog,
  console: mdiConsoleLine,
  'content-copy': mdiContentCopy,
  delete: mdiDelete,
  'dots-horizontal': mdiDotsHorizontal,
  download: mdiDownload,
  eye: mdiEye,
  'eye-off': mdiEyeOff,
  filmstrip: mdiFilmstrip,
  flash: mdiFlash,
  folder: mdiFolder,
  'information-outline': mdiInformationOutline,
  'lan-connect': mdiLanConnect,
  magnify: mdiMagnify,
  menu: mdiMenu,
  play: mdiPlay,
  plus: mdiPlus,
  record: mdiRecord,
  refresh: mdiRefresh,
  robot: mdiRobot,
  server: mdiServer,
  stop: mdiStop,
  'theme-dark': mdiWeatherNight,
  'theme-light': mdiWhiteBalanceSunny,
  'theme-system': mdiThemeLightDark,
  video: mdiVideo,
}

/**
 * Quasar 的 <q-icon name="play"> / <q-btn icon="play"> 走 iconMapFn 解析：
 * 返回 { icon: path } 表示「把 name 换成这段 SVG path」，返回 undefined 则退回内置行为
 * （内置行为是 material-icons 连字字体，本项目不加载字体，等于不显示）。
 * 未登记的名字在开发态给出提示，避免又变成「图标空白」。
 */
export function iconMapFn(name: string): { icon: string } | undefined {
  const path = customIcons[name]
  if (path === undefined) {
    if (import.meta.env.DEV) {
      console.warn(`[icons] 未登记的图标名：${name}`)
    }
    return undefined
  }
  return { icon: path }
}
