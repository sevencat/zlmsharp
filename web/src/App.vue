<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { navItems } from './router'
import { auth, clearLogs, logs, saveSecret } from './api'
import { appIconUrl } from './branding'
import { refresh, state, startPolling, stopPolling } from './store'
import AppIcon from './components/AppIcon.vue'

const route = useRoute()
const secretInput = ref(auth.secret)
const showLogs = ref(false)

const currentLabel = computed(() => navItems.find((item) => route.path.startsWith(item.path))?.label ?? '总览')
const statusClass = computed(() => (state.versionError || state.mediaError ? 'is-error' : 'is-ok'))
const statusText = computed(() => {
  const error = state.versionError || state.mediaError
  if (error) return error
  if (!state.updatedAt) return '连接中…'
  return `已连接 · 最近更新 ${new Date(state.updatedAt).toLocaleTimeString()}`
})

function applySecret() {
  saveSecret(secretInput.value)
  secretInput.value = auth.secret
  void refresh()
}

onMounted(() => startPolling(5000))
onBeforeUnmount(() => stopPolling())
</script>

<template>
  <div class="app-shell">
    <aside class="app-side">
      <div class="brand">
        <img class="brand-logo" :src="appIconUrl" alt="zlmsharp" />
        <span>zlmsharp</span>
      </div>

      <nav>
        <router-link
          v-for="item in navItems"
          :key="item.path"
          :to="item.path"
          class="nav-item"
          active-class="is-active"
        >
          <AppIcon :icon="item.icon" size="small" />
          <span>{{ item.label }}</span>
        </router-link>
      </nav>

      <div class="side-foot">
        <template v-if="state.version">
          <div>{{ state.version.buildTime }}</div>
          <div>{{ state.version.branchName }} / {{ state.version.commitHash }}</div>
        </template>
        <template v-else>版本信息不可用</template>
      </div>
    </aside>

    <div class="app-main">
      <header class="app-top">
        <div class="top-left">
          <span class="dot" :class="statusClass"></span>
          <strong>{{ currentLabel }}</strong>
          <span>{{ statusText }}</span>
        </div>

        <div class="top-right">
          <b-field>
            <b-input
              v-model="secretInput"
              type="password"
              password-reveal
              size="is-small"
              placeholder="api.secret（回环可留空）"
              @keyup.enter="applySecret"
            />
          </b-field>
          <b-button size="is-small" type="is-primary" @click="applySecret">保存</b-button>
          <b-button size="is-small" icon-left="refresh" :loading="state.loading" @click="refresh">刷新</b-button>
        </div>
      </header>

      <main class="app-content">
        <router-view />
      </main>

      <footer class="app-log" :class="{ 'is-open': showLogs }">
        <button class="log-toggle" type="button" @click="showLogs = !showLogs">
          <AppIcon :icon="showLogs ? 'chevron-down' : 'chevron-up'" size="small" />
          请求日志（{{ logs.entries.length }}）
          <span v-if="!showLogs" class="muted">· 点击展开</span>
        </button>

        <div v-if="showLogs" class="log-body">
          <div class="log-actions">
            <b-button size="is-small" @click="clearLogs">清空</b-button>
          </div>
          <ul>
            <li v-for="entry in logs.entries" :key="entry.id" :class="entry.level">
              <span class="at">{{ entry.at }}</span>
              <span class="title">{{ entry.title }}</span>
              <pre v-if="entry.detail">{{ entry.detail }}</pre>
            </li>
            <li v-if="logs.entries.length === 0" class="muted">暂无请求</li>
          </ul>
        </div>
      </footer>
    </div>
  </div>
</template>
