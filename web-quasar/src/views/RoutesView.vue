<template>
  <div class="column q-gutter-y-md">
    <div class="panel">
      <div class="panel-head">
        <q-icon name="broadcast" size="18px" class="muted" />
        <div class="panel-title">拉流代理</div>
        <span class="muted">RTSP / RTMP → 本地流</span>
        <q-space />
        <q-btn dense flat round icon="refresh" :loading="busy === 'loadProxy'" @click="loadProxies">
          <q-tooltip>刷新列表</q-tooltip>
        </q-btn>
      </div>
      <q-separator />

      <div class="q-pa-md">
        <div class="row q-col-gutter-sm items-center">
          <div class="col-12 col-sm-2">
            <q-input v-model="proxyForm.app" dense outlined label="app" />
          </div>
          <div class="col-12 col-sm-2">
            <q-input v-model="proxyForm.stream" dense outlined label="stream" />
          </div>
          <div class="col-12 col-sm">
            <q-input
              v-model="proxyForm.url"
              dense
              outlined
              label="源地址"
              placeholder="rtsp://user:pass@host/stream 或 rtmp://..."
              @keyup.enter="addProxy"
            />
          </div>
          <div class="col-auto">
            <q-btn unelevated dense no-caps color="primary" icon="plus" label="添加" :loading="busy === 'addProxy'" @click="addProxy" />
          </div>
        </div>

        <div class="row q-col-gutter-md items-center q-mt-xs">
          <div class="col-auto">
            <q-toggle v-model="proxyForm.keep" dense color="primary" label="常驻（无人观看也不回收）" :disable="proxyForm.onDemand" />
          </div>
          <div class="col-auto">
            <q-toggle v-model="proxyForm.onDemand" dense color="primary" label="按需拉流（有人点播才拉）" />
          </div>
        </div>
      </div>

      <q-table
        :rows="proxies"
        :columns="proxyColumns"
        row-key="key"
        flat
        dense
        :rows-per-page-options="[0]"
        hide-bottom
        no-data-label="暂无代理"
        class="q-px-sm q-pb-sm"
      >
        <template #body-cell-stream="cell">
          <q-td :props="cell">
            <div class="mono">{{ cell.row.app }}/{{ cell.row.stream }}</div>
            <div class="muted mono">{{ cell.row.vhost }}</div>
          </q-td>
        </template>
        <template #body-cell-url="cell">
          <q-td :props="cell">
            <div class="mono ellipsis" style="max-width: 380px">{{ cell.row.url }}</div>
          </q-td>
        </template>
        <template #body-cell-state="cell">
          <q-td :props="cell">
            <q-badge v-if="cell.row.online" color="positive">正在拉流</q-badge>
            <q-badge v-else-if="cell.row.onDemand" color="info">待点播</q-badge>
            <q-badge v-else color="grey-6">离线</q-badge>
          </q-td>
        </template>
        <template #body-cell-elapsed="cell">
          <q-td :props="cell">{{ cell.row.online ? seconds(Math.round(cell.row.elapsedMs / 1000)) : '-' }}</q-td>
        </template>
        <template #body-cell-keep="cell">
          <q-td :props="cell">
            <q-icon v-if="cell.row.keepOnNoneReader" name="check" size="18px" class="text-positive" />
            <span v-else class="muted">-</span>
          </q-td>
        </template>
        <template #body-cell-actions="cell">
          <q-td :props="cell">
            <div class="table-actions">
              <q-btn
                flat
                dense
                round
                size="sm"
                icon="delete"
                color="negative"
                :loading="busy === cell.row.key"
                @click="removeProxy(cell.row.key)"
              >
                <q-tooltip>删除代理</q-tooltip>
              </q-btn>
            </div>
          </q-td>
        </template>
      </q-table>
    </div>

    <div class="panel">
      <div class="panel-head">
        <q-icon name="flash" size="18px" class="muted" />
        <div class="panel-title">RTMP 转推</div>
        <span class="muted">把本地流推到远端 RTMP 服务器</span>
        <q-space />
        <q-btn dense flat round icon="refresh" :loading="busy === 'loadPush'" @click="loadPushes">
          <q-tooltip>刷新列表</q-tooltip>
        </q-btn>
      </div>
      <q-separator />

      <div class="q-pa-md">
        <div class="row q-col-gutter-sm items-center">
          <div class="col-12 col-sm-2">
            <q-input v-model="pushForm.app" dense outlined label="app" />
          </div>
          <div class="col-12 col-sm-2">
            <q-input v-model="pushForm.stream" dense outlined label="stream" />
          </div>
          <div class="col-12 col-sm">
            <q-input
              v-model="pushForm.url"
              dense
              outlined
              label="目标地址"
              placeholder="rtmp://host/app/stream"
              @keyup.enter="addPush"
            />
          </div>
          <div class="col-auto">
            <q-btn unelevated dense no-caps color="primary" icon="play" label="开始转推" :loading="busy === 'addPush'" @click="addPush" />
          </div>
        </div>

        <q-banner v-if="pushError" dense rounded class="bg-negative text-white q-mt-md">
          <template #avatar>
            <q-icon name="alert" />
          </template>
          {{ pushError }}
        </q-banner>
      </div>

      <q-table
        :rows="pushes"
        :columns="pushColumns"
        row-key="url"
        flat
        dense
        :rows-per-page-options="[0]"
        hide-bottom
        no-data-label="暂无转推"
        class="q-px-sm q-pb-sm"
      >
        <template #body-cell-stream="cell">
          <q-td :props="cell">
            <div class="mono">{{ cell.row.app }}/{{ cell.row.stream }}</div>
          </q-td>
        </template>
        <template #body-cell-url="cell">
          <q-td :props="cell">
            <div class="mono ellipsis" style="max-width: 380px">{{ cell.row.url }}</div>
          </q-td>
        </template>
        <template #body-cell-state="cell">
          <q-td :props="cell">
            <q-badge :color="cell.row.error ? 'negative' : 'positive'">
              {{ cell.row.error ? '异常' : pushStateText(cell.row.state) }}
            </q-badge>
            <div v-if="cell.row.error" class="muted mono ellipsis" style="max-width: 320px">{{ cell.row.error }}</div>
          </q-td>
        </template>
        <template #body-cell-elapsed="cell">
          <q-td :props="cell">{{ seconds(Math.round(cell.row.elapsedMs / 1000)) }}</q-td>
        </template>
        <template #body-cell-actions="cell">
          <q-td :props="cell">
            <div class="table-actions">
              <q-btn
                flat
                dense
                round
                size="sm"
                icon="stop"
                color="negative"
                :loading="busy === cell.row.url"
                @click="stopPush(cell.row)"
              >
                <q-tooltip>停止转推</q-tooltip>
              </q-btn>
            </div>
          </q-td>
        </template>
      </q-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import type { QTableColumn } from 'quasar'
import { call, callList } from '../api'
import { seconds } from '../store'
import { toast, toastError } from '../notify'
import type { ProxyState, PushState } from '../types'

const proxies = ref<ProxyState[]>([])
const pushes = ref<PushState[]>([])
const busy = ref('')
const pushError = ref('')

const proxyForm = reactive({ app: 'live', stream: '', url: '', keep: false, onDemand: false })
const pushForm = reactive({ app: 'live', stream: '', url: '' })

const proxyColumns: QTableColumn[] = [
  { name: 'stream', label: '流', field: 'stream', align: 'left', sortable: true },
  { name: 'url', label: '源地址', field: 'url', align: 'left' },
  { name: 'state', label: '状态', field: 'online', align: 'left' },
  { name: 'elapsed', label: '已运行', field: 'elapsedMs', align: 'left', sortable: true },
  { name: 'keep', label: '常驻', field: 'keepOnNoneReader', align: 'center' },
  { name: 'actions', label: '操作', field: 'key', align: 'right' },
]

const pushColumns: QTableColumn[] = [
  { name: 'stream', label: '流', field: 'stream', align: 'left', sortable: true },
  { name: 'url', label: '目标地址', field: 'url', align: 'left' },
  { name: 'state', label: '状态', field: 'state', align: 'left' },
  { name: 'frames', label: '帧数', field: 'frames', align: 'left', sortable: true },
  { name: 'elapsed', label: '已运行', field: 'elapsedMs', align: 'left', sortable: true },
  { name: 'actions', label: '操作', field: 'url', align: 'right' },
]

const pushStateText = (state: string) => {
  if (state === 'running') return '推送中'
  if (state === 'starting') return '启动中'
  if (state === 'stopped') return '已停止'
  if (state === 'failed') return '失败'
  return state
}

async function loadProxies() {
  busy.value = 'loadProxy'
  try {
    proxies.value = await callList<ProxyState>('getProxyList')
  } catch {
    // 失败详情已写入请求日志
  } finally {
    busy.value = ''
  }
}

async function addProxy() {
  if (!proxyForm.stream.trim() || !proxyForm.url.trim()) return
  busy.value = 'addProxy'
  try {
    await call('addStreamProxy', {
      app: proxyForm.app,
      stream: proxyForm.stream,
      url: proxyForm.url,
      keep: !proxyForm.onDemand && proxyForm.keep ? 1 : undefined,
      on_demand: proxyForm.onDemand ? 1 : undefined,
    })
    proxyForm.stream = ''
    proxyForm.url = ''
    toast('success', proxyForm.onDemand ? '已登记按需拉流（有人点播时才拉）' : '拉流代理已添加')
    await loadProxies()
  } catch (error) {
    toastError(error, '添加代理')
  } finally {
    busy.value = ''
  }
}

async function removeProxy(key: string) {
  busy.value = key
  try {
    await call('delStreamProxy', { key })
    toast('success', '代理已删除')
    await loadProxies()
  } catch (error) {
    toastError(error, '删除代理')
  } finally {
    busy.value = ''
  }
}

async function loadPushes() {
  busy.value = 'loadPush'
  try {
    pushes.value = await callList<PushState>('getPushList')
  } catch {
    // 同上
  } finally {
    busy.value = ''
  }
}

async function addPush() {
  if (!pushForm.stream.trim() || !pushForm.url.trim()) return
  busy.value = 'addPush'
  pushError.value = ''
  try {
    await call('startPush', { app: pushForm.app, stream: pushForm.stream, url: pushForm.url })
    pushForm.stream = ''
    pushForm.url = ''
    toast('success', '转推已开始')
    await loadPushes()
  } catch (error) {
    // 同时写入请求日志；这里额外留在表单下方，避免失败看起来“没反应”
    pushError.value = error instanceof Error ? error.message : String(error)
    toastError(error, '开始转推')
  } finally {
    busy.value = ''
  }
}

async function stopPush(item: PushState) {
  busy.value = item.url
  try {
    await call('stopPush', { app: item.app, stream: item.stream })
    toast('success', '转推已停止')
    await loadPushes()
  } catch (error) {
    toastError(error, '停止转推')
  } finally {
    busy.value = ''
  }
}

onMounted(() => {
  loadProxies()
  loadPushes()
})
</script>
