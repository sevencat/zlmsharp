// 品牌图标来自仓库根 assets/app-icon.png（512x512），构建前缩成 128x128 放到这里。
// Vite 的 assetsInlineLimit: Infinity 会把它直接转成 base64 data URI，
// 因此产物里不会多出图片文件，浏览器也不会有额外请求。
import appIcon from './assets/app-icon.png'

export const appIconUrl: string = appIcon
