<template>
  <div class="column q-gutter-y-md">
    <div class="panel">
      <div class="panel-head">
        <q-icon name="filmstrip" size="18px" class="muted" />
        <div class="panel-title">录像查询</div>
        <q-space />
        <q-input v-model="app" dense outlined placeholder="app" class="short-input" />
        <q-input v-model="stream" dense outlined placeholder="stream" class="short-input" @keyup.enter="query" />
        <q-select
          v-model="recordFormat"
          :options="formatOptions"
          dense
          outlined
          emit-value
          map-options
          options-dense
          label="录像格式"
          class="format-select"
        />
        <q-btn dense unelevated color="primary" no-caps icon="magnify" label="查询" @click="query" />
        <q-btn
          dense
          outline
          color="negative"
          no-caps
          icon="record"
          label="开始录像"
          :loading="busy === 'start'"
          :disable="!app.trim() || !stream.trim() || recordingOn"
          @click="toggleRecord(true)"
        />
        <q-btn
          dense
          outline
          color="negative"
          no-caps
          icon="stop"
          label="停止录像"
          :loading="busy === 'stop'"
          :disable="!recordingOn"
          @click="toggleRecord(false)"
        />
        <q-btn dense flat round icon="refresh" :loading="loading" @click="browse(currentPath)">
          <q-tooltip>刷新当前目录</q-tooltip>
        </q-btn>
      </div>
      <q-separator />

      <div class="row items-center q-px-md q-py-sm">
        <q-breadcrumbs active-color="primary" class="mono" gutter="xs">
          <q-breadcrumbs-el label="根目录" @click="goTo(0)" />
          <q-breadcrumbs-el v-for="(seg, index) in segments" :key="seg" :label="seg" @click="goTo(index + 1)" />
        </q-breadcrumbs>
        <q-space />
        <q-btn v-if="segments.length" dense flat no-caps size="sm" icon="arrow-up" label="上一级" @click="up" />
      </div>

      <q-banner v-if="error" dense rounded class="bg-negative text-white q-mx-md q-mb-sm">
        <template #avatar>
          <q-icon name="alert" />
        </template>
        {{ error }}
      </q-banner>

      <q-table
        :rows="rows"
        :columns="columns"
        row-key="name"
        flat
        dense
        :loading="loading"
        :rows-per-page-options="[0]"
        hide-bottom
        :no-data-label="'该目录下没有内容'"
        class="q-px-sm q-pb-sm"
      >
        <template #body-cell-name="cell">
          <q-td :props="cell">
            <div class="row items-center no-wrap q-gutter-x-sm">
              <q-icon :name="cell.row.kind === 'dir' ? 'folder' : 'filmstrip'" size="18px" :class="cell.row.kind === 'dir' ? 'text-amber-8' : 'muted'" />
              <span class="mono ellipsis">{{ cell.row.name }}</span>
            </div>
          </q-td>
        </template>

        <template #body-cell-kind="cell">
          <q-td :props="cell">
            <q-badge :color="cell.row.kind === 'dir' ? 'amber-8' : 'blue-grey'" outline>
              {{ cell.row.kind === 'dir' ? '目录' : '文件' }}
            </q-badge>
          </q-td>
        </template>

        <template #body-cell-actions="cell">
          <q-td :props="cell">
            <div class="table-actions">
              <q-btn
                v-if="cell.row.kind === 'dir'"
                flat
                dense
                no-caps
                size="sm"
                icon="folder"
                label="进入"
                color="primary"
                @click="enter(cell.row.name)"
              />
              <template v-else>
                <q-btn flat dense round size="sm" icon="play" color="primary" @click="playRecord(cell.row.name)">
                  <q-tooltip>播放</q-tooltip>
                </q-btn>
                <q-btn flat dense round size="sm" icon="download" :href="recordUrl(cell.row.name)" :download="cell.row.name">
                  <q-tooltip>下载</q-tooltip>
                </q-btn>
                <q-btn
                  flat
                  dense
                  round
                  size="sm"
                  icon="delete"
                  color="negative"
                  :loading="busy === cell.row.name"
                  @click="remove(cell.row.name)"
                >
                  <q-tooltip>删除</q-tooltip>
                </q-btn>
              </template>
            </div>
          </q-td>
        </template>
      </q-table>
    </div>

    <div v-if="recordings.length" class="panel">
      <div class="panel-head">
        <q-icon name="record" size="18px" class="text-negative" />
        <div class="panel-title">该流的录像任务</div>
        <q-badge color="negative" outline :label="String(recordings.length)" />
      </div>
      <q-separator />
      <div class="row q-col-gutter-md q-pa-md">
        <div v-for="item in recordings" :key="`${item.vhost}/${item.app}/${item.stream}/${item.state}`" class="col-12 col-md-6">
          <div class="record-item">
            <div class="row items-center no-wrap">
              <span class="mono">{{ item.app }}/{{ item.stream }}</span>
              <q-space />
              <q-badge :color="item.state === 'recording' ? 'negative' : 'grey-6'">{{ stateText(item.state) }}</q-badge>
            </div>
            <div class="muted mono q-mt-xs">
              帧 {{ item.frames }} · {{ seconds(Math.round(item.durationMs / 1000)) }}
            </div>
            <div class="muted mono ellipsis">{{ item.mp4Path || item.flvPath || item.hlsPath || '-' }}</div>
          </div>
        </div>
      </div>
    </div>

    <q-dialog v-model="playActive">
      <q-card class="player-card">
        <q-card-section class="row items-center q-pb-none">
          <q-icon name="filmstrip" size="18px" class="q-mr-sm text-primary" />
          <div class="mono ellipsis">{{ playing?.name }}</div>
          <q-space />
          <q-btn flat dense round icon="close" v-close-popup />
        </q-card-section>
        <q-card-section class="q-pt-sm">
          <RecordPlayer v-if="playing" :url="playing.url" />
        </q-card-section>
      </q-card>
    </q-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { QTableColumn } from 'quasar'
import { call, callList } from '../api'
import { seconds } from '../store'
import { toast, toastError } from '../notify'
import { confirmAction } from '../confirm'
import RecordPlayer from '../components/RecordPlayer.vue'
import type { RecordFileData, RecordState } from '../types'

const app = ref('live')
const stream = ref('')
const loading = ref(false)
const error = ref('')
const data = ref<RecordFileData | null>(null)
const busy = ref('')
/** 当前浏览的相对目录（相对录像根目录，'/' 分隔） */
const currentPath = ref('')
const recordings = ref<RecordState[]>([])
/** 录像格式：1=MP4 2=FLV 3=MP4+FLV 4=HLS */
const recordFormat = ref(3)
const playing = ref<{ name: string; url: string } | null>(null)

const formatOptions = [
  { label: 'MP4 + FLV', value: 3 },
  { label: '仅 MP4', value: 1 },
  { label: '仅 FLV', value: 2 },
  { label: 'HLS', value: 4 },
]

const columns: QTableColumn[] = [
  { name: 'name', label: '名称', field: 'name', align: 'left', sortable: true },
  { name: 'kind', label: '类型', field: 'kind', align: 'left' },
  { name: 'actions', label: '操作', field: 'name', align: 'right' },
]

const playActive = computed({
  get: () => playing.value !== null,
  set: (value: boolean) => {
    if (!value) playing.value = null
  },
})

/** 该流当前是否在录像（用于按钮状态） */
const recordingOn = computed(() =>
  recordings.value.some((item) => item.state === 'recording' || item.state === 'starting'),
)

const stateText = (state: string) => {
  if (state === 'recording') return '录制中'
  if (state === 'starting') return '启动中'
  if (state === 'stopped') return '已停止'
  if (state === 'failed') return '失败'
  return state
}

const rows = computed(() =>
  (data.value?.paths ?? []).map((name) => ({ name, kind: data.value?.directories ? 'dir' : 'file' })),
)

const pathSegments = () =>
  (currentPath.value ?? '')
    .split('/')
    .filter((item) => item.length > 0)

const segments = computed(() => pathSegments())

const recordUrl = (name: string) =>
  `/record/${pathSegments().map(encodeURIComponent).join('/')}/${encodeURIComponent(name)}`

const playRecord = (name: string) => {
  playing.value = { name, url: recordUrl(name) }
}

/** path 为空且 filter=false 时浏览录像根目录（列出各 app） */
async function browse(path: string, filter = false) {
  loading.value = true
  error.value = ''
  try {
    const params: Record<string, unknown> = {}
    if (path) {
      params.path = path
    } else if (filter) {
      params.app = app.value
      params.stream = stream.value
    }

    const result = await call<RecordFileData>('getMp4RecordFile', params)
    data.value = {
      rootPath: result?.rootPath ?? '',
      paths: Array.isArray(result?.paths) ? result.paths : [],
      directories: result?.directories === true,
    }
    currentPath.value = data.value.rootPath
  } catch (exception) {
    error.value = exception instanceof Error ? exception.message : String(exception)
    data.value = null
  } finally {
    loading.value = false
  }
}

async function query() {
  if (!app.value.trim() && !stream.value.trim()) {
    error.value = '请填写 app 或 stream，或直接点目录浏览'
    return
  }
  await browse('', true)
  await loadRecordings()
}

async function enter(name: string) {
  if (!data.value) return
  const next = currentPath.value ? `${currentPath.value}/${name}` : name
  await browse(next)
}

async function up() {
  if (!currentPath.value) return
  const parts = currentPath.value.split('/')
  parts.pop()
  await browse(parts.join('/'))
}

async function goTo(depth: number) {
  if (depth <= 0) {
    await browse('')
    return
  }
  await browse(segments.value.slice(0, depth).join('/'))
}

async function remove(name: string) {
  const ok = await confirmAction(`确定删除录像文件 ${name}？该操作不可恢复。`, '删除录像')
  if (!ok) return

  busy.value = name
  try {
    const path = currentPath.value ? `${currentPath.value}/${name}` : name
    await call('deleteRecordFile', { app: app.value, stream: stream.value, path })
    toast('success', '删除成功')
    await browse(currentPath.value)
  } catch (error) {
    toastError(error, '删除录像')
  } finally {
    busy.value = ''
  }
}

async function loadRecordings() {
  try {
    recordings.value = await callList<RecordState>('getRecordList', { stream: stream.value })
  } catch {
    recordings.value = []
  }
}

async function toggleRecord(start: boolean) {
  busy.value = start ? 'start' : 'stop'
  try {
    if (start) {
      const status = await call<RecordState>('startRecord', {
        app: app.value,
        stream: stream.value,
        type: recordFormat.value,
      })
      const file = status.mp4Path ?? status.flvPath ?? status.hlsPath
      toast('success', file ? `开始录像：${file}` : '开始录像成功', 6000)
      await loadRecordings()
      // 起录后直接跳到该流目录，能看到刚生成的文件
      await browse(`${app.value}/${stream.value}`)
    } else {
      await call('stopRecord', { app: app.value, stream: stream.value })
      toast('success', '停止录像成功')
      await loadRecordings()
      await browse(currentPath.value)
    }
  } catch (error) {
    toastError(error, start ? '开始录像' : '停止录像')
  } finally {
    busy.value = ''
  }
}

onMounted(async () => {
  await loadRecordings()
  await browse('')
})
</script>
