import { createRouter, createWebHashHistory } from 'vue-router'
import DashboardView from './views/DashboardView.vue'
import StreamsView from './views/StreamsView.vue'
import SessionsView from './views/SessionsView.vue'
import RecordsView from './views/RecordsView.vue'
import RoutesView from './views/RoutesView.vue'
import AiView from './views/AiView.vue'
import ApiDebugView from './views/ApiDebugView.vue'

export interface NavItem {
  path: string
  label: string
  icon: string
  caption: string
  group: string
}

/** 侧边栏导航项（顺序即显示顺序） */
export const navItems: NavItem[] = [
  { path: '/dashboard', label: '总览', icon: 'chart-line', caption: '概览与运行信息', group: '监控' },
  { path: '/streams', label: '在线流', icon: 'video', caption: '播放 / 录像 / 叠加', group: '监控' },
  { path: '/sessions', label: '会话', icon: 'account-multiple', caption: '推流与播放连接', group: '监控' },
  { path: '/records', label: '录像', icon: 'filmstrip', caption: '文件浏览与回放', group: '媒体' },
  { path: '/routes', label: '代理 / 转推', icon: 'broadcast', caption: '拉流代理与 RTMP 转推', group: '媒体' },
  { path: '/ai', label: 'AI 分析', icon: 'robot', caption: '分析器与叠加流', group: '智能' },
  { path: '/api', label: 'API 调试', icon: 'api', caption: '直接调用 /index/api', group: '智能' },
]

/** 按 group 分组后的菜单，供侧边栏渲染分组标题 */
export const navGroups = navItems.reduce<{ name: string; items: NavItem[] }[]>((acc, item) => {
  const last = acc[acc.length - 1]
  if (last && last.name === item.group) {
    last.items.push(item)
  } else {
    acc.push({ name: item.group, items: [item] })
  }
  return acc
}, [])

// 单文件页面部署在 "/"，没有任何 SPA 回退路由，必须用 hash 模式
export default createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/', redirect: '/dashboard' },
    { path: '/dashboard', component: DashboardView },
    { path: '/streams', component: StreamsView },
    { path: '/sessions', component: SessionsView },
    { path: '/records', component: RecordsView },
    { path: '/routes', component: RoutesView },
    { path: '/ai', component: AiView },
    { path: '/api', component: ApiDebugView },
    { path: '/:pathMatch(.*)*', redirect: '/dashboard' },
  ],
})
