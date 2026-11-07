<template>
  <q-layout view="hHh Lpr fFf" class="app-layout">
    <q-header class="app-header">
      <q-toolbar class="q-px-sm q-px-md-md">
        <q-btn flat dense round icon="menu" class="q-mr-xs" @click="drawer = !drawer">
          <q-tooltip>功能菜单</q-tooltip>
        </q-btn>

        <q-toolbar-title class="app-title">
          {{ currentLabel }}
          <div class="app-subtitle">
            <span class="status-dot" :class="statusClass" />
            <span class="ellipsis">{{ statusText }}</span>
          </div>
        </q-toolbar-title>

        <q-space />

        <div class="header-tools row items-center no-wrap q-gutter-x-xs">
          <q-input
            v-model="secretInput"
            dense
            outlined
            clearable
            placeholder="secret"
            class="secret-input gt-xs"
            input-class="mono"
            @keyup.enter="applySecret"
          >
            <q-tooltip>接口鉴权 secret（非本机访问时必填）</q-tooltip>
          </q-input>

          <q-btn flat dense round icon="check" :disable="secretInput === auth.secret" @click="applySecret">
            <q-tooltip>保存 secret</q-tooltip>
          </q-btn>

          <q-btn flat dense round icon="refresh" :loading="state.loading" @click="manualRefresh">
            <q-tooltip>立即刷新</q-tooltip>
          </q-btn>

          <q-btn flat dense round icon="console" :color="showLogs ? 'primary' : undefined" @click="showLogs = !showLogs">
            <q-tooltip>请求日志（{{ logs.entries.length }}）</q-tooltip>
          </q-btn>

          <q-btn flat dense round :icon="isDark ? 'theme-light' : 'theme-dark'" @click="toggleTheme">
            <q-tooltip>{{ isDark ? '切换到浅色' : '切换到深色' }}</q-tooltip>
          </q-btn>
        </div>
      </q-toolbar>

      <!-- 窄屏：顶部横向功能菜单，保证任何宽度下都能看到全部入口 -->
      <q-tabs
        v-if="isNarrow"
        dense
        align="left"
        narrow-indicator
        no-caps
        class="app-tabs"
        active-color="white"
        indicator-color="white"
      >
        <q-route-tab v-for="item in navItems" :key="item.path" :to="item.path" :label="item.label" />
      </q-tabs>
    </q-header>

    <!-- persistent：禁止 QDrawer 在路由变化时自动收起（窄屏由脚本控制） -->
    <q-drawer v-model="drawer" :width="248" :breakpoint="1023" persistent bordered class="side-drawer">
      <div class="brand">
        <img :src="appIconUrl" class="brand-logo" alt="zlmsharp" />
        <div>
          <div class="brand-name">zlmsharp</div>
          <div class="brand-sub">流媒体控制台</div>
        </div>
      </div>

      <q-separator dark inset />

      <q-list padding class="q-py-sm">
        <template v-for="group in navGroups" :key="group.name">
          <q-item-label header class="nav-group">{{ group.name }}</q-item-label>
          <q-item
            v-for="item in group.items"
            :key="item.path"
            :to="item.path"
            clickable
            v-ripple
            class="nav-item"
            active-class="nav-active"
            @click="onNavClick"
          >
            <q-item-section avatar>
              <q-icon :name="item.icon" size="20px" />
            </q-item-section>
            <q-item-section>
              <q-item-label>{{ item.label }}</q-item-label>
              <q-item-label caption lines="1">{{ item.caption }}</q-item-label>
            </q-item-section>
          </q-item>
        </template>
      </q-list>

      <div class="side-foot">
        <div v-if="state.version">构建 {{ state.version.buildTime }}</div>
        <div v-if="state.version">{{ state.version.branchName }} @ {{ shortCommit }}</div>
        <div v-else class="text-grey-7">版本信息不可用</div>
      </div>
    </q-drawer>

    <q-dialog v-model="showLogs" position="bottom" full-width>
      <q-card class="log-sheet">
        <div class="row items-center q-px-md q-py-xs">
          <q-icon name="console" size="18px" class="q-mr-sm" />
          <span class="text-subtitle2">请求日志</span>
          <q-badge color="grey-6" class="q-ml-sm" :label="String(logs.entries.length)" />
          <q-space />
          <q-btn flat dense no-caps size="sm" label="清空" :disable="!logs.entries.length" @click="clearLogs" />
          <q-btn flat dense round icon="chevron-down" @click="showLogs = false" />
        </div>
        <q-separator />
        <div class="log-body">
          <div v-for="entry in logs.entries" :key="entry.id" class="log-row">
            <span class="log-at mono">{{ entry.at }}</span>
            <span class="log-title mono" :class="`is-${entry.level}`">{{ entry.title }}</span>
            <pre v-if="entry.detail" class="mono">{{ entry.detail }}</pre>
          </div>
          <div v-if="!logs.entries.length" class="muted q-py-md text-center">暂无请求记录</div>
        </div>
      </q-card>
    </q-dialog>

    <q-page-container>
      <q-page class="page">
        <router-view />
      </q-page>
    </q-page-container>
  </q-layout>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useQuasar } from 'quasar'
import { useRoute } from 'vue-router'
import { auth, clearLogs, logs, saveSecret } from './api'
import { appIconUrl } from './branding'
import { navGroups, navItems } from './router'
import { refresh, startPolling, state, stopPolling } from './store'
import { initTheme, isDark, toggleTheme } from './theme'
import { toast } from './notify'

const route = useRoute()
const $q = useQuasar()

// 宽屏默认展开菜单（只依赖窗口宽度，不依赖 Quasar 的 show-if-above 时序）
const drawer = ref(window.innerWidth > 1023)
const showLogs = ref(false)
const secretInput = ref(auth.secret)

/** 窄屏标记：直接读 window.innerWidth，避免与 Quasar screen 的初始化时序耦合 */
const isNarrow = ref(window.innerWidth <= 1023)

function onNavClick() {
  if (isNarrow.value) drawer.value = false
}

/** 宽屏常驻展开、窄屏收起 */
function syncDrawer() {
  isNarrow.value = window.innerWidth <= 1023
  drawer.value = !isNarrow.value
}

const currentLabel = computed(
  () => navItems.find((item) => route.path.startsWith(item.path))?.label ?? '总览',
)

const shortCommit = computed(() => state.version?.commitHash?.slice(0, 8) ?? '')

const statusClass = computed(() => {
  if (state.mediaError || state.versionError) return 'is-error'
  if (state.loading) return ''
  return state.media.length ? 'is-ok' : ''
})

const statusText = computed(() => {
  if (state.versionError) return `后端未就绪：${state.versionError}`
  if (state.mediaError) return `读取在线流失败：${state.mediaError}`
  const streams = state.media.length
  const readers = state.media.reduce((sum, item) => sum + (item.readerCount ?? 0), 0)
  const updated = state.updatedAt ? new Date(state.updatedAt).toLocaleTimeString() : '-'
  return `${streams} 路在线 · ${readers} 个观看 · 更新于 ${updated}`
})

function applySecret() {
  saveSecret(secretInput.value ?? '')
  secretInput.value = auth.secret
  toast('success', 'secret 已保存')
  void manualRefresh()
}

async function manualRefresh() {
  try {
    await refresh()
    toast('success', '数据已刷新', 1600)
  } catch (error) {
    toast('error', error instanceof Error ? error.message : String(error), 5000)
  }
}

// 路由切换时收起窄屏抽屉（宽屏保持展开，菜单始终可见）
watch(
  () => route.path,
  () => {
    if (isNarrow.value) drawer.value = false
  },
)

let syncTimers: number[] = []

onMounted(() => {
  syncDrawer()
  // 视口变化可能晚于首次渲染（外部工具改尺寸时不会补发事件），补两次同步
  syncTimers = [window.setTimeout(syncDrawer, 200), window.setTimeout(syncDrawer, 800)]
  window.addEventListener('resize', syncDrawer)
  initTheme()
  startPolling(5000)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', syncDrawer)
  syncTimers.forEach((timer) => clearTimeout(timer))
  syncTimers = []
  stopPolling()
})
</script>
