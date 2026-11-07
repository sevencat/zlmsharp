<template>
  <div class="panel">
    <div class="panel-head">
      <q-icon name="account-multiple" size="18px" class="muted" />
      <div class="panel-title">会话</div>
      <q-badge color="primary" outline :label="`${sessions.length} 个 / 推流 ${publishers}`" />
      <q-space />
      <q-input v-model="filter" dense outlined clearable placeholder="过滤 id / 流 / IP" class="filter-input">
        <template #prepend>
          <q-icon name="magnify" size="18px" />
        </template>
      </q-input>
      <q-btn dense flat icon="refresh" no-caps label="加载" :loading="loading" @click="load" />
    </div>
    <q-separator />

    <q-table
      :rows="rows"
      :columns="columns"
      row-key="id"
      flat
      dense
      :loading="loading"
      :rows-per-page-options="[0]"
      hide-bottom
      no-data-label="暂无会话"
      class="q-px-sm q-pb-sm"
    >
      <template #body-cell-type="cell">
        <q-td :props="cell">
          <q-badge :color="cell.row.type === 'publish' ? 'orange-8' : 'primary'">
            {{ cell.row.type === 'publish' ? '推流' : '播放' }}
          </q-badge>
        </q-td>
      </template>

      <template #body-cell-id="cell">
        <q-td :props="cell">
          <div class="mono ellipsis" style="max-width: 260px">{{ cell.row.id }}</div>
          <div class="muted mono">{{ cell.row.schema }}</div>
        </q-td>
      </template>

      <template #body-cell-stream="cell">
        <q-td :props="cell">
          <div class="mono">{{ cell.row.app }}/{{ cell.row.stream }}</div>
          <div class="muted mono">{{ cell.row.vhost }}</div>
        </q-td>
      </template>

      <template #body-cell-client="cell">
        <q-td :props="cell">
          <div class="mono">{{ cell.row.ip }}:{{ cell.row.port }}</div>
          <div class="muted ellipsis" style="max-width: 220px">{{ cell.row.userAgent || '-' }}</div>
        </q-td>
      </template>

      <template #body-cell-alive="cell">
        <q-td :props="cell">{{ seconds(cell.row.aliveSecond) }}</q-td>
      </template>

      <template #body-cell-actions="cell">
        <q-td :props="cell">
          <div class="table-actions">
            <q-btn
              flat
              dense
              round
              size="sm"
              icon="close"
              color="negative"
              :loading="busy === cell.row.id"
              @click="kick(cell.row)"
            >
              <q-tooltip>踢出会话</q-tooltip>
            </q-btn>
          </div>
        </q-td>
      </template>
    </q-table>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { QTableColumn } from 'quasar'
import { call, callList, log } from '../api'
import { seconds } from '../store'
import { toast, toastError } from '../notify'
import { confirmAction } from '../confirm'
import type { SessionInfo } from '../types'

const sessions = ref<SessionInfo[]>([])
const loading = ref(false)
const filter = ref('')
const busy = ref('')

const columns: QTableColumn[] = [
  { name: 'type', label: '类型', field: 'type', align: 'left', sortable: true },
  { name: 'id', label: 'ID / 协议', field: 'id', align: 'left' },
  { name: 'stream', label: '流', field: 'stream', align: 'left', sortable: true },
  { name: 'client', label: '客户端', field: 'ip', align: 'left' },
  { name: 'alive', label: '存活', field: 'aliveSecond', align: 'left', sortable: true },
  { name: 'actions', label: '操作', field: 'id', align: 'right' },
]

async function load() {
  loading.value = true
  try {
    sessions.value = await callList<SessionInfo>('getSessionList')
  } catch {
    // 失败详情已写入请求日志
  } finally {
    loading.value = false
  }
}

async function kick(item: SessionInfo) {
  const ok = await confirmAction(`确定踢出会话 ${item.id}？`, '踢出会话')
  if (!ok) return

  busy.value = item.id
  try {
    await call('kick_session', { id: item.id })
    toast('success', `已踢出会话 ${item.id}`)
    log('info', `已踢出会话 ${item.id}`)
    await load()
  } catch (error) {
    toastError(error, '踢出会话')
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
