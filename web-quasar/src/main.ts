import { createApp } from 'vue'
import { Quasar, Notify, Dialog } from 'quasar'
import type { QuasarPluginOptions } from 'quasar'
// Quasar 自带 svg-mdi-v7：把内部图标映射成 @mdi/js 的 SVG path，不依赖任何图标字体
import mdiIconSet from 'quasar/icon-set/svg-mdi-v7.js'
import 'quasar/src/css/index.sass'
import './styles/app.scss'
import App from './App.vue'
import router from './router'
import { customIcons, iconMapFn } from './icons'
import { appIconUrl } from './branding'

// 单文件页面：favicon 用内联 data URI，避免任何外部请求
const favicon = document.createElement('link')
favicon.rel = 'icon'
favicon.type = 'image/png'
favicon.href = appIconUrl
document.head.appendChild(favicon)

const app = createApp(App)

app.use(Quasar, {
  plugins: { Notify, Dialog },
  // 官方 SVG 图标集只覆盖组件内部用到的图标，模板里的名字由 iconMapFn 补上
  iconSet: { ...mdiIconSet, ...customIcons },
  // iconMapFn 在运行时从 config 读取（Quasar 类型声明里挂在 iconSet 上，故此处断言）
  config: {
    iconMapFn,
    notify: { position: 'top-right', timeout: 3200 },
  } as QuasarPluginOptions['config'],
})

app.use(router)
app.mount('#app')
