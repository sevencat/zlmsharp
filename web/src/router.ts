import { createRouter, createWebHashHistory } from 'vue-router'
import DashboardView from './views/DashboardView.vue'
import StreamsView from './views/StreamsView.vue'
import SessionsView from './views/SessionsView.vue'
import RecordsView from './views/RecordsView.vue'
import RoutesView from './views/RoutesView.vue'
import AiView from './views/AiView.vue'
import ApiDebugView from './views/ApiDebugView.vue'

/** 侧边栏导航项（顺序即显示顺序） */
export const navItems = [
  { path: '/dashboard', label: '总览', icon: 'chart-line' },
  { path: '/streams', label: '在线流', icon: 'video' },
  { path: '/sessions', label: '会话', icon: 'account-multiple' },
  { path: '/records', label: '录像', icon: 'filmstrip' },
  { path: '/routes', label: '代理 / 转推', icon: 'broadcast' },
  { path: '/ai', label: 'AI 分析', icon: 'robot' },
  { path: '/api', label: 'API 调试', icon: 'api' },
]

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
