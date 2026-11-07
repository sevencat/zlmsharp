<template>
  <div class="column q-gutter-y-md">
    <div class="panel">
      <div class="panel-head">
        <q-icon name="robot" size="18px" class="muted" />
        <div class="panel-title">分析器</div>
        <q-space />
        <q-btn dense flat round icon="refresh" :loading="busy === 'analyzers'" @click="loadAnalyzers">
          <q-tooltip>刷新</q-tooltip>
        </q-btn>
      </div>
      <q-separator />
      <q-table
        :rows="analyzers"
        :columns="analyzerColumns"
        row-key="id"
        flat
        dense
        :rows-per-page-options="[0]"
        hide-bottom
        no-data-label="未安装 / 未启用 AI 分析扩展"
        class="q-px-sm q-pb-sm"
      >
        <template #body-cell-name="cell">
          <q-td :props="cell">
            <div>{{ cell.row.displayName }}</div>
            <div class="muted mono">{{ cell.row.id }}</div>
          </q-td>
        </template>
        <template #body-cell-state="cell">
          <q-td :props="cell">
            <q-badge :color="analyzerColor(cell.row)">
              {{ !cell.row.enabled ? '未启用' : cell.row.ready ? '就绪' : '未加载' }}
            </q-badge>
          </q-td>
        </template>
        <template #body-cell-model="cell">
          <q-td :props="cell">
            <div class="mono">{{ cell.row.model || '-' }}</div>
            <div class="muted mono ellipsis" style="max-width: 260px">{{ cell.row.modelDir }}</div>
          </q-td>
        </template>
        <template #body-cell-device="cell">
          <q-td :props="cell" class="mono">{{ cell.row.device || '-' }}</q-td>
        </template>
        <template #body-cell-backend="cell">
          <q-td :props="cell" class="mono">{{ cell.row.backend || '-' }}</q-td>
        </template>
        <template #body-cell-labels="cell">
          <q-td :props="cell">{{ cell.row.labels?.length ?? 0 }}</q-td>
        </template>
        <template #body-cell-note="cell">
          <q-td :props="cell">
            <div v-if="cell.row.error" class="text-negative mono">{{ cell.row.error }}</div>
            <div v-else class="muted mono ellipsis" style="max-width: 320px">
              已加载模型：{{ cell.row.loadedModels?.join(', ') || '-' }}
            </div>
          </q-td>
        </template>
      </q-table>
    </div>

    <div class="panel">
      <div class="panel-head">
        <q-icon name="chart-line" size="18px" class="muted" />
        <div class="panel-title">分析结果</div>
        <q-space />
        <q-input v-model="stream" dense outlined placeholder="stream（留空=全部）" class="filter-input" @keyup.enter="loadResults" />
        <q-input v-model="analyzerFilter" dense outlined placeholder="analyzer（留空=全部）" class="filter-input" @keyup.enter="loadResults" />
        <q-btn dense unelevated color="primary" no-caps icon="magnify" label="查询" :loading="busy === 'results'" @click="loadResults" />
      </div>
      <q-separator />
      <q-table
        :rows="results"
        :columns="resultColumns"
        row-key="rowKey"
        flat
        dense
        wrap-cells
        :rows-per-page-options="[0]"
        hide-bottom
        no-data-label="暂无分析结果（先推流并启用分析器）"
        class="q-px-sm q-pb-sm"
      >
        <template #body-cell-stream="cell">
          <q-td :props="cell" class="mono">{{ cell.row.app }}/{{ cell.row.stream }}</q-td>
        </template>
        <template #body-cell-analyzer="cell">
          <q-td :props="cell">
            <q-badge color="deep-purple" outline>{{ cell.row.analyzer }}</q-badge>
          </q-td>
        </template>
        <template #body-cell-size="cell">
          <q-td :props="cell" class="mono">{{ cell.row.width }}x{{ cell.row.height }}</q-td>
        </template>
        <template #body-cell-cost="cell">
          <q-td :props="cell">{{ Math.round(cell.row.elapsedMs) }} ms</q-td>
        </template>
        <template #body-cell-count="cell">
          <q-td :props="cell">
            <q-badge :color="cell.row.count > 0 ? 'primary' : 'grey-5'" :label="String(cell.row.count)" />
          </q-td>
        </template>
        <template #body-cell-content="cell">
          <q-td :props="cell">
            <div v-if="cell.row.error" class="text-negative mono">{{ cell.row.error }}</div>
            <div v-else class="row q-gutter-xs">
              <q-chip
                v-for="(prim, index) in cell.row.primitives.slice(0, 6)"
                :key="index"
                dense
                square
                size="sm"
                color="blue-grey-1"
                text-color="blue-grey-9"
              >
                {{ prim.label }} {{ Math.round((prim.score || 0) * 100) }}%
              </q-chip>
              <span v-if="cell.row.primitives.length > 6" class="muted">+{{ cell.row.primitives.length - 6 }}</span>
              <span v-if="!cell.row.primitives.length" class="muted">无目标</span>
            </div>
          </q-td>
        </template>
        <template #body-cell-age="cell">
          <q-td :props="cell">{{ Math.round(cell.row.ageMs) }} ms 前</q-td>
        </template>
      </q-table>
    </div>

    <div class="panel">
      <div class="panel-head">
        <q-icon name="filmstrip" size="18px" class="muted" />
        <div class="panel-title">叠加会话</div>
        <q-space />
        <q-btn dense flat round icon="refresh" :loading="busy === 'overlay'" @click="loadOverlays">
          <q-tooltip>刷新</q-tooltip>
        </q-btn>
      </div>
      <q-separator />

      <div class="q-pa-md">
        <div class="row q-col-gutter-sm items-center">
          <div class="col-12 col-sm-2">
            <q-input v-model="overlayForm.app" dense outlined label="app" />
          </div>
          <div class="col-12 col-sm-2">
            <q-input v-model="overlayForm.stream" dense outlined label="stream" />
          </div>
          <div class="col-12 col-sm-3">
            <q-select
              v-model="overlayForm.analyzers"
              :options="analyzerOptions"
              dense
              outlined
              multiple
              use-chips
              emit-value
              map-options
              options-dense
              label="分析器（空=全部）"
            />
          </div>
          <div class="col-12 col-sm-2">
            <q-select
              v-model="overlayForm.model"
              :options="yoloModels"
              dense
              outlined
              clearable
              options-dense
              label="模型（可选）"
            />
          </div>
          <div class="col-12 col-sm-1">
            <q-input v-model.number="overlayForm.fps" dense outlined type="number" label="fps" />
          </div>
          <div class="col-auto">
            <q-btn
              unelevated
              dense
              no-caps
              color="primary"
              icon="play"
              label="启动"
              :loading="busy === 'start'"
              :disable="!overlayForm.stream.trim()"
              @click="startOverlay"
            />
          </div>
        </div>
      </div>

      <q-table
        :rows="overlays"
        :columns="overlayColumns"
        row-key="overlayStream"
        flat
        dense
        :rows-per-page-options="[0]"
        hide-bottom
        no-data-label="暂无叠加会话"
        class="q-px-sm q-pb-sm"
      >
        <template #body-cell-stream="cell">
          <q-td :props="cell">
            <div class="mono">源 {{ cell.row.app }}/{{ cell.row.stream }}</div>
            <div class="muted mono">输出 {{ cell.row.overlayStream }}</div>
          </q-td>
        </template>
        <template #body-cell-state="cell">
          <q-td :props="cell">
            <q-badge :color="overlayColor(cell.row.state)">{{ overlayText(cell.row.state) }}</q-badge>
            <div v-if="cell.row.error" class="muted mono ellipsis" style="max-width: 280px">{{ cell.row.error }}</div>
          </q-td>
        </template>
        <template #body-cell-size="cell">
          <q-td :props="cell" class="mono">{{ cell.row.width }}x{{ cell.row.height }}</q-td>
        </template>
        <template #body-cell-frames="cell">
          <q-td :props="cell">{{ cell.row.frames }}</q-td>
        </template>
        <template #body-cell-elapsed="cell">
          <q-td :props="cell">{{ seconds(Math.round(cell.row.elapsedMs / 1000)) }}</q-td>
        </template>
        <template #body-cell-actions="cell">
          <q-td :props="cell">
            <div class="table-actions">
              <q-btn flat dense round size="sm" icon="play" color="primary" @click="play(cell.row)">
                <q-tooltip>播放叠加流</q-tooltip>
              </q-btn>
              <q-btn
                flat
                dense
                round
                size="sm"
                icon="stop"
                color="negative"
                :loading="busy === `stop-${cell.row.overlayStream}`"
                @click="stopOverlay(cell.row)"
              >
                <q-tooltip>停止叠加</q-tooltip>
              </q-btn>
            </div>
          </q-td>
        </template>
      </q-table>
    </div>

    <q-dialog v-model="playActive">
      <q-card class="player-card">
        <q-card-section class="row items-center q-pb-none">
          <q-icon name="robot" size="18px" class="q-mr-sm text-primary" />
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
import { computed, onMounted, reactive, ref } from 'vue'
import type { QTableColumn } from 'quasar'
import { call, callList } from '../api'
import { seconds } from '../store'
import { toast, toastError } from '../notify'
import FlvPlayer from '../components/FlvPlayer.vue'
import type { AnalysisData, AnalyzerInfo, OverlayState } from '../types'

const analyzers = ref<AnalyzerInfo[]>([])
const results = ref<AnalysisData[]>([])
const overlays = ref<OverlayState[]>([])
const busy = ref('')
const stream = ref('')
const analyzerFilter = ref('')
const playing = ref<{ app: string; stream: string } | null>(null)

const overlayForm = reactive({ app: 'live', stream: '', analyzers: [] as string[], model: '', fps: 0 })

const analyzerColumns: QTableColumn[] = [
  { name: 'name', label: '分析器', field: 'displayName', align: 'left' },
  { name: 'state', label: '状态', field: 'enabled', align: 'left' },
  { name: 'model', label: '模型', field: 'model', align: 'left' },
  { name: 'device', label: '设备', field: 'device', align: 'left' },
  { name: 'backend', label: '后端', field: 'backend', align: 'left' },
  { name: 'labels', label: '类别数', field: 'labels', align: 'left' },
  { name: 'note', label: '说明', field: 'error', align: 'left' },
]

const resultColumns: QTableColumn[] = [
  { name: 'stream', label: '流', field: 'stream', align: 'left', sortable: true },
  { name: 'analyzer', label: '分析器', field: 'analyzer', align: 'left' },
  { name: 'size', label: '尺寸', field: 'width', align: 'left' },
  { name: 'cost', label: '耗时', field: 'elapsedMs', align: 'left', sortable: true },
  { name: 'count', label: '目标', field: 'count', align: 'left', sortable: true },
  { name: 'content', label: '内容', field: 'primitives', align: 'left' },
  { name: 'age', label: '时间', field: 'ageMs', align: 'left', sortable: true },
]

const overlayColumns: QTableColumn[] = [
  { name: 'stream', label: '流', field: 'stream', align: 'left' },
  { name: 'state', label: '状态', field: 'state', align: 'left' },
  { name: 'size', label: '尺寸', field: 'width', align: 'left' },
  { name: 'frames', label: '帧数', field: 'frames', align: 'left', sortable: true },
  { name: 'elapsed', label: '已运行', field: 'elapsedMs', align: 'left', sortable: true },
  { name: 'actions', label: '操作', field: 'overlayStream', align: 'right' },
]

/** YOLO 可选模型（来自 getAnalyzerList 的 models 字段，即 ZLM_YOLO_MODEL_DIR 下的文件） */
const yoloModels = computed(() => analyzers.value.find((item) => item.id === 'yolo')?.models ?? [])

const analyzerOptions = computed(() =>
  analyzers.value.map((item) => ({ label: `${item.displayName}（${item.id}）`, value: item.id })),
)

const analyzerColor = (item: AnalyzerInfo) => (!item.enabled ? 'grey-6' : item.ready ? 'positive' : 'warning')

const overlayText = (state: string) => {
  if (state === 'running') return '运行中'
  if (state === 'starting') return '预热中'
  if (state === 'failed') return '失败'
  if (state === 'stopped') return '已停止'
  return state
}

const overlayColor = (state: string) => {
  if (state === 'running') return 'positive'
  if (state === 'starting') return 'warning'
  if (state === 'failed') return 'negative'
  return 'grey-6'
}

const playActive = computed({
  get: () => playing.value !== null,
  set: (value: boolean) => {
    if (!value) playing.value = null
  },
})

const play = (item: OverlayState) => {
  playing.value = { app: item.app, stream: item.overlayStream }
}

async function guard(label: string, action: () => Promise<unknown>) {
  busy.value = label
  try {
    await action()
  } catch (error) {
    toastError(error)
  } finally {
    busy.value = ''
  }
}

const loadAnalyzers = () =>
  guard('analyzers', async () => (analyzers.value = await callList<AnalyzerInfo>('getAnalyzerList')))

const loadResults = () =>
  guard('results', async () => {
    const list = await callList<AnalysisData>('analyzeResult', {
      stream: stream.value,
      analyzer: analyzerFilter.value,
    })
    results.value = list.map((item) => ({ ...item, rowKey: `${item.vhost}/${item.app}/${item.stream}/${item.analyzer}` }))
  })

const loadOverlays = () => guard('overlay', async () => (overlays.value = await callList<OverlayState>('getOverlay')))

const startOverlay = () =>
  guard('start', async () => {
    const status = await call<OverlayState>('startOverlay', {
      app: overlayForm.app,
      stream: overlayForm.stream,
      analyzer: overlayForm.analyzers.length > 0 ? overlayForm.analyzers.join(',') : undefined,
      model: overlayForm.model || undefined,
      fps: overlayForm.fps || undefined,
    })
    toast('success', `叠加流已启动：${status.overlayStream}`, 6000)
    await loadOverlays()
  })

const stopOverlay = (item: OverlayState) =>
  guard(`stop-${item.overlayStream}`, async () => {
    await call('stopOverlay', { app: item.app, stream: item.stream })
    toast('success', `已停止叠加：${item.overlayStream}`)
    await loadOverlays()
  })

onMounted(async () => {
  await loadAnalyzers()
  await loadResults()
  await loadOverlays()
})
</script>
