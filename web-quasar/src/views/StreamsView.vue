<template>
  <div class="column q-gutter-y-md">
    <q-banner v-if="state.mediaError" dense rounded class="bg-negative text-white">
      <template #avatar>
        <q-icon name="alert" />
      </template>
      {{ state.mediaError }}
    </q-banner>

    <div class="panel">
      <div class="panel-head">
        <q-icon name="video" size="18px" class="muted" />
        <div class="panel-title">在线流</div>
        <q-badge color="primary" outline :label="`${rows.length} / ${state.media.length}`" />
        <q-space />
        <q-input v-model="keyword" dense outlined clearable placeholder="过滤 app / stream / 编码" class="filter-input">
          <template #prepend>
            <q-icon name="magnify" size="18px" />
          </template>
        </q-input>
        <q-select
          v-model="recordFormat"
          :options="formatOptions"
          dense
          outlined
          emit-value
          map-options
          label="录像格式"
          options-dense
          class="format-select"
        />
        <q-btn dense flat round icon="refresh" :loading="state.loading" @click="reload">
          <q-tooltip>刷新列表</q-tooltip>
        </q-btn>
      </div>
      <q-separator />

      <q-table
        :rows="rows"
        :columns="columns"
        row-key="rowKey"
        flat
        dense
        wrap-cells
        :rows-per-page-options="[0]"
        hide-bottom
        no-data-label="暂无在线流"
        class="q-px-sm q-pb-sm"
      >
        <template #body-cell-stream="cell">
          <q-td :props="cell">
            <div class="mono">{{ cell.row.app }}/{{ cell.row.stream }}</div>
            <div class="row q-gutter-xs q-mt-xs">
              <q-badge v-if="overlayOf(cell.row)" color="deep-purple" outline label="叠加输出" />
              <q-badge v-else-if="isProxy(cell.row)" color="indigo" outline label="代理" />
              <span class="muted mono">{{ cell.row.vhost }} · {{ cell.row.schema }}</span>
            </div>
          </q-td>
        </template>

        <template #body-cell-codec="cell">
          <q-td :props="cell">
            <span>{{ cell.row.videoCodec || '-' }}</span>
            <span v-if="cell.row.audioCodec" class="muted">/ {{ cell.row.audioCodec }}</span>
            <div class="muted mono">{{ cell.row.videoFps ? `${cell.row.videoFps} fps` : '' }}</div>
          </q-td>
        </template>

        <template #body-cell-resolution="cell">
          <q-td :props="cell" class="mono">{{ resolution(cell.row) }}</q-td>
        </template>

        <template #body-cell-origin="cell">
          <q-td :props="cell">
            <q-badge :color="originColor(cell.row.originType)" :label="cell.row.originType || 'unknown'" />
          </q-td>
        </template>

        <template #body-cell-readers="cell">
          <q-td :props="cell">
            <q-badge :color="cell.row.readerCount > 0 ? 'primary' : 'grey-5'" :label="String(cell.row.readerCount)" />
          </q-td>
        </template>

        <template #body-cell-alive="cell">
          <q-td :props="cell">{{ seconds(cell.row.aliveSecond) }}</q-td>
        </template>

        <template #body-cell-drops="cell">
          <q-td :props="cell">
            <span :class="cell.row.dropFrames > 0 ? 'text-negative' : 'muted'">{{ cell.row.dropFrames || 0 }}</span>
          </q-td>
        </template>

        <template #body-cell-recording="cell">
          <q-td :props="cell">
            <q-badge v-if="recordingOf(cell.row)" color="negative">
              录制中
              <q-tooltip v-if="recordingTitle(cell.row)" class="mono">{{ recordingTitle(cell.row) }}</q-tooltip>
            </q-badge>
            <span v-else class="muted">-</span>
          </q-td>
        </template>

        <template #body-cell-actions="cell">
          <q-td :props="cell">
            <div class="table-actions">
              <q-btn flat dense round size="sm" icon="play" color="primary" @click="play(cell.row)">
                <q-tooltip>播放</q-tooltip>
              </q-btn>

              <q-btn
                v-if="!recordingOf(cell.row)"
                flat
                dense
                round
                size="sm"
                icon="record"
                color="negative"
                :loading="busy === `rec:${cell.row.rowKey}`"
                :disable="!!busy && busy !== `rec:${cell.row.rowKey}`"
                @click="startRecord(cell.row)"
              >
                <q-tooltip>开始录像</q-tooltip>
              </q-btn>
              <q-btn
                v-else
                flat
                dense
                round
                size="sm"
                icon="stop"
                color="negative"
                :loading="busy === `stoprec:${cell.row.rowKey}`"
                @click="stopRecord(cell.row)"
              >
                <q-tooltip>停止录像</q-tooltip>
              </q-btn>

              <q-btn
                v-if="!overlayOf(cell.row)"
                flat
                dense
                round
                size="sm"
                icon="robot"
                color="deep-purple"
                :loading="busy === `overlay:${cell.row.rowKey}`"
                @click="startOverlay(cell.row)"
              >
                <q-tooltip>启动 AI 叠加</q-tooltip>
              </q-btn>
              <q-btn
                v-else
                flat
                dense
                round
                size="sm"
                icon="close"
                color="deep-purple"
                @click="stopOverlay(cell.row)"
              >
                <q-tooltip>停止叠加</q-tooltip>
              </q-btn>

              <q-btn
                flat
                dense
                round
                size="sm"
                icon="delete"
                color="grey-7"
                :loading="busy === `close:${cell.row.rowKey}`"
                @click="closeStream(cell.row)"
              >
                <q-tooltip>{{ isProxy(cell.row) ? '停止拉流（删除代理）' : '关闭流' }}</q-tooltip>
              </q-btn>
            </div>
          </q-td>
        </template>
      </q-table>
    </div>

    <q-dialog v-model="playActive">
      <q-card class="player-card">
        <q-card-section class="row items-center q-pb-none">
          <q-icon name="play" size="18px" class="q-mr-sm text-primary" />
          <div class="mono">{{ playing ? `${playing.app}/${playing.stream}` : '' }}</div>
          <q-space />
          <q-btn flat dense round icon="close" v-close-popup />
        </q-card-section>
        <q-card-section class="q-pt-sm">
          <FlvPlayer v-if="playing" :app="playing.app" :stream="playing.stream" />
        </q-card-section>
      </q-card>
    </q-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import type { QTableColumn } from 'quasar'
import { call, log } from '../api'
import { refresh, seconds, state } from '../store'
import { toast, toastError } from '../notify'
import { confirmAction } from '../confirm'
import FlvPlayer from '../components/FlvPlayer.vue'
import type { MediaInfo, OverlayState, RecordState } from '../types'

const keyword = ref('')
/** 正在执行的操作用 "<动作>:<行key>" 标记，避免同一时刻乱点 */
const busy = ref('')
const playing = ref<{ app: string; stream: string } | null>(null)
/** 录像格式：1=MP4 2=FLV 3=MP4+FLV 4=HLS */
const recordFormat = ref(3)

const formatOptions = [
  { label: 'MP4 + FLV', value: 3 },
  { label: '仅 MP4', value: 1 },
  { label: '仅 FLV', value: 2 },
  { label: 'HLS', value: 4 },
]

const playActive = computed({
  get: () => playing.value !== null,
  set: (value: boolean) => {
    if (!value) playing.value = null
  },
})

const rows = computed(() => {
  const kw = keyword.value.trim().toLowerCase()
  const list = kw
    ? state.media.filter((item) =>
        [item.app, item.stream, item.vhost, item.schema, item.originType, item.videoCodec, item.audioCodec]
          .join(' ')
          .toLowerCase()
          .includes(kw),
      )
    : state.media
  return list.map((item) => ({ ...item, rowKey: `${item.vhost}/${item.app}/${item.stream}` }))
})

const columns: QTableColumn[] = [
  { name: 'stream', label: '流', field: 'stream', align: 'left', sortable: true },
  { name: 'codec', label: '编码', field: 'videoCodec', align: 'left' },
  { name: 'resolution', label: '分辨率', field: 'videoWidth', align: 'left' },
  { name: 'origin', label: '来源', field: 'originType', align: 'left' },
  { name: 'readers', label: '观看', field: 'readerCount', align: 'left', sortable: true },
  { name: 'alive', label: '存活', field: 'aliveSecond', align: 'left', sortable: true },
  { name: 'drops', label: '丢帧', field: 'dropFrames', align: 'left', sortable: true },
  { name: 'recording', label: '录像', field: 'stream', align: 'left' },
  { name: 'actions', label: '操作', field: 'stream', align: 'right' },
]

const resolution = (item: MediaInfo) => (item.videoWidth ? `${item.videoWidth}x${item.videoHeight}` : '-')

const originColor = (type: string) => {
  const key = (type || '').toLowerCase()
  if (key === 'rtmp') return 'red-7'
  if (key === 'rtsp') return 'orange-8'
  if (key === 'hls') return 'green-7'
  if (key === 'proxy') return 'indigo'
  if (key === 'pull') return 'deep-purple'
  return 'blue-grey'
}

/** 正在录制的流：key = vhost/app/stream */
const recordingMap = computed(() => {
  const map = new Map<string, RecordState>()
  for (const item of state.recordings) {
    if (item.state === 'recording' || item.state === 'starting') {
      map.set(`${item.vhost}/${item.app}/${item.stream}`, item)
    }
  }
  return map
})

const recordingOf = (item: MediaInfo) => recordingMap.value.get(`${item.vhost}/${item.app}/${item.stream}`)

const recordingTitle = (item: MediaInfo) => {
  const record = recordingOf(item)
  if (!record) return undefined
  const files = [record.mp4Path, record.flvPath, record.hlsPath].filter(Boolean).join('\n')
  return files || undefined
}

async function run(label: string, token: string, action: () => Promise<unknown>) {
  busy.value = token
  try {
    await action()
    toast('success', `${label}成功`)
    log('info', `${label} 完成`)
    await refresh()
  } catch (error) {
    toastError(error, label)
  } finally {
    busy.value = ''
  }
}

async function reload() {
  try {
    await refresh()
    toast('success', '已刷新', 1500)
  } catch (error) {
    toastError(error, '刷新')
  }
}

const play = (item: MediaInfo) => {
  playing.value = { app: item.app, stream: item.stream }
}

const startRecord = (item: MediaInfo) =>
  run('开始录像', `rec:${item.vhost}/${item.app}/${item.stream}`, async () => {
    const status = await call<RecordState>('startRecord', {
      app: item.app,
      stream: item.stream,
      type: recordFormat.value,
    })
    const file = status.mp4Path ?? status.flvPath ?? status.hlsPath
    if (file) {
      toast('info', `录像文件：${file}`, 6000)
    }
  })

const stopRecord = (item: MediaInfo) =>
  run('停止录像', `stoprec:${item.vhost}/${item.app}/${item.stream}`, () =>
    call('stopRecord', { app: item.app, stream: item.stream }),
  )

const startOverlay = (item: MediaInfo) =>
  run('AI 叠加', `overlay:${item.vhost}/${item.app}/${item.stream}`, async () => {
    const status = await call<OverlayState>('startOverlay', { app: item.app, stream: item.stream })
    toast('info', `叠加流已启动：${status.overlayStream}`, 6000)
  })

/** 该行是某路叠加会话的输出流时返回会话（用会话里的源流名去停止） */
const overlayOf = (item: MediaInfo) =>
  state.overlays.find(
    (overlay) => overlay.app === item.app && overlay.vhost === item.vhost && overlay.overlayStream === item.stream,
  )

const isProxy = (item: MediaInfo) => (item.originType || '').toLowerCase() === 'proxy'

async function closeStream(item: MediaInfo) {
  const proxy = isProxy(item)
  const ok = await confirmAction(
    proxy
      ? `确定停止拉流 ${item.app}/${item.stream}？会删除这条代理。`
      : `确定关闭流 ${item.app}/${item.stream}？推流端需要重新推流。`,
    proxy ? '停止拉流' : '关闭流',
  )
  if (!ok) return

  if (proxy) {
    await run('停止拉流', `close:${item.vhost}/${item.app}/${item.stream}`, () =>
      call('delStreamProxy', { vhost: item.vhost, app: item.app, stream: item.stream }),
    )
    return
  }

  await run('关闭流', `close:${item.vhost}/${item.app}/${item.stream}`, () =>
    call('close_streams', { vhost: item.vhost, app: item.app, stream: item.stream }),
  )
}

async function stopOverlay(item: MediaInfo) {
  const overlay = overlayOf(item)
  if (!overlay) {
    toast('error', '叠加会话已结束，请刷新')
    return
  }
  const ok = await confirmAction(`确定停止叠加 ${overlay.overlayStream}？`, '停止叠加')
  if (!ok) return

  await run('停止叠加', `close:${item.vhost}/${item.app}/${item.stream}`, () =>
    call('stopOverlay', { vhost: overlay.vhost, app: overlay.app, stream: overlay.stream }),
  )
}
</script>
