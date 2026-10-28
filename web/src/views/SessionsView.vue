<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { call, log } from '../api'
import { seconds } from '../store'
import type { SessionInfo } from '../types'

const sessions = ref<SessionInfo[]>([])
const loading = ref(false)
const filter = ref('')
const busy = ref('')

async function load() {
  loading.value = true
  try {
    sessions.value = await call<SessionInfo[]>('getSessionList')
  } catch {
    // 失败详情已写入日志
  } finally {
    loading.value = false
  }
}

async function kick(item: SessionInfo) {
  busy.value = item.id
  try {
    await call('kick_session', { id: item.id })
    log('info', `已踢出会话 ${item.id}`)
    await load()
  } catch {
    // 同上
  } finally {
    busy.value = ''
  }
}

const rows = computed(() => {
  const kw = filter.value.trim().toLowerCase()
  if (!kw) return sessions.value
  return sessions.value.filter((item) =>
    [item.id, item.type, item.schema, item.app, item.stream, item.ip].join(' ').toLowerCase().includes(kw),
  )
})

const publishers = computed(() => sessions.value.filter((item) => item.type === 'publish').length)

onMounted(load)
</script>

<template>
  <section class="panel">
    <header>
      <h2>会话（{{ sessions.length }}，其中推流 {{ publishers }}）</h2>
      <div class="toolbar">
        <b-input v-model="filter" size="is-small" icon="magnify" placeholder="过滤 id / 流 / IP" clearable />
        <b-button size="is-small" icon-left="refresh" :loading="loading" @click="load">加载</b-button>
      </div>
    </header>

    <div class="table-wrap">
      <table class="table is-fullwidth is-hoverable">
        <thead>
          <tr><th>ID</th><th>类型</th><th>协议</th><th>流</th><th>对端</th><th>存活</th><th>UA</th><th>操作</th></tr>
        </thead>
        <tbody>
          <tr v-for="item in rows" :key="item.id">
            <td class="mono">{{ item.id }}</td>
            <td>
              <b-tag size="is-small" :type="item.type === 'publish' ? 'is-primary is-light' : 'is-success is-light'">
                {{ item.type }}
              </b-tag>
            </td>
            <td>{{ item.schema || '-' }}</td>
            <td class="mono">{{ item.app ? `${item.app}/${item.stream}` : '-' }}</td>
            <td>{{ item.ip || '-' }}:{{ item.port }}</td>
            <td>{{ seconds(item.aliveSecond) }}</td>
            <td class="muted" :title="item.userAgent ?? ''">
              {{ item.userAgent ? item.userAgent.slice(0, 28) : '-' }}
            </td>
            <td class="actions">
              <b-button
                size="is-small"
                type="is-danger is-light"
                :loading="busy === item.id"
                @click="kick(item)"
              >踢出</b-button>
            </td>
          </tr>
          <tr v-if="rows.length === 0"><td colspan="8" class="empty">没有会话</td></tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
