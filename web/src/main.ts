import { createApp } from 'vue'
import Buefy from 'buefy'
// Buefy 3.x 的预编译样式（已内含 Bulma）：dist/css/buefy.css
import 'buefy/dist/css/buefy.css'
import './styles/app.css'
import App from './App.vue'
import router from './router'
import MdiIcon from './components/MdiIcon.vue'
import { appIconUrl } from './branding'

// 单文件页面：favicon 用内联 data URI，避免任何外部请求
const favicon = document.createElement('link')
favicon.rel = 'icon'
favicon.type = 'image/png'
favicon.href = appIconUrl
document.head.appendChild(favicon)

const app = createApp(App)

// Buefy 不内置图标字体：注册一个「不同名」的 SVG 图标组件，交给 defaultIconComponent 接管。
// 注意两个坑：
//   1. defaultIconComponent 必须是组件名字符串，不是组件对象；
//   2. 绝不能写成 'BIcon'，因为 BIcon 在 defaultIconComponent 有值时会渲染该组件自身 → 无限递归爆栈。
app.component('ZlmIcon', MdiIcon)

app.use(Buefy as any, {
  defaultIconPack: 'mdi',
  defaultIconComponent: 'ZlmIcon',
})

app.use(router)
app.mount('#app')
