<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { call } from '../api'
import { toast, toastError } from '../toast'
import FlvPlayer from '../components/FlvPlayer.vue'
import type { AnalysisData, AnalyzerInfo, OverlayState } from '../types'

const analyzers = ref<AnalyzerInfo[]>([])
const results = ref<AnalysisData[]>([])
const overlays = ref<OverlayState[]>([])
const busy = ref('')
const stream = ref('')
const analyzerFilter = ref('')
const playing = ref<{ app: string; stream: string } | null>(null)

const overlayForm = reactive({ app: 'live', stream: '', analyzer: '', fps: 0 })

const playActive = computed({
  get: () => playing.value !== null,
  set: (value: boolean) => {
    if (!value) playing.value = null
  },
})

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
  guard('analyzers', async () => (analyzers.value = await call<AnalyzerInfo[]>('getAnalyzerList')))

const loadResults = () =>
  guard('results', async () => {
    results.value = await call<AnalysisData[]>('analyzeResult', {
      stream: stream.value,
      analyzer: analyzerFilter.value,
    })
  })

const loadOverlays = () => guard('overlay', async () => (overlays.value = await call<OverlayState[]>('getOverlay')))

const startOverlay = () =>
  guard('start', async () => {
    const status = await call<OverlayState>('startOverlay', {
      app: overlayForm.app,
      stream: overlayForm.stream,
      analyzer: overlayForm.analyzer || undefined,
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

<template>
  <div>
    <section class="panel">
      <header>
        <h2>分析器</h2>
        <b-button size="is-small" icon-left="refresh" :loading="busy === 'analyzers'" @click="loadAnalyzers">刷新</b-button>
      </header>

      <div class="table-wrap">
        <table class="table is-fullwidth is-hoverable">
          <thead>
            <tr><th>分析器</th><th>状态</th><th>模型</th><th>设备</th><th>后端</th><th>类别数</th><th>说明</th></tr>
          </thead>
          <tbody>
            <tr v-for="item in analyzers" :key="item.id">
              <td class="mono">{{ item.displayName }}（{{ item.id }}）</td>
              <td>
                <b-tag
                  size="is-small"
                  :type="item.enabled ? (item.ready ? 'is-success is-light' : 'is-warning is-light') : 'is-danger is-light'"
                >
                  {{ item.enabled ? (item.ready ? '就绪' : '未加载') : '未启用' }}
                </b-tag>
              </td>
              <td class="mono">{{ item.model || '-' }}</td>
              <td class="mono">{{ item.device || '-' }}</td>
              <td class="mono">{{ item.backend || '-' }}</td>
              <td>{{ item.labels.length }}</td>
              <td class="muted">{{ item.error ?? '-' }}</td>
            </tr>
            <tr v-if="analyzers.length === 0">
              <td colspan="7" class="empty">没有分析器（未接入 ZlmSharp.Extra，或未注册任何分析器）</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <section class="panel">
      <header>
        <h2>分析结果</h2>
        <div class="toolbar">
          <b-select v-model="analyzerFilter" size="is-small" placeholder="分析器（全部）">
            <option value="">全部</option>
            <option v-for="item in analyzers" :key="item.id" :value="item.id">{{ item.displayName }}</option>
          </b-select>
          <b-input v-model="stream" size="is-small" placeholder="stream（留空=全部）" clearable />
          <b-button size="is-small" :loading="busy === 'results'" @click="loadResults">查询</b-button>
        </div>
      </header>

      <div class="table-wrap">
        <table class="table is-fullwidth is-hoverable">
          <thead><tr><th>分析器</th><th>流</th><th>尺寸</th><th>目标数</th><th>耗时</th><th>结果年龄</th><th>错误</th></tr></thead>
          <tbody>
            <tr v-for="item in results" :key="`${item.analyzer}/${item.app}/${item.stream}`">
              <td class="mono">{{ item.analyzer }}</td>
              <td class="mono">{{ item.app }}/{{ item.stream }}</td>
              <td>{{ item.width }}x{{ item.height }}</td>
              <td>{{ item.count }}</td>
              <td>{{ item.elapsedMs }}ms</td>
              <td>{{ Math.round(item.ageMs / 1000) }}s</td>
              <td class="muted">{{ item.error ?? '-' }}</td>
            </tr>
            <tr v-if="results.length === 0"><td colspan="7" class="empty">暂无分析结果</td></tr>
          </tbody>
        </table>
      </div>

      <p v-if="results[0]?.primitives.length" class="muted">
        首个结果的图元：
        <b-tag
          v-for="primitive in results[0].primitives.slice(0, 12)"
          :key="`${primitive.trackId}-${primitive.label}`"
          size="is-small"
          type="is-info is-light"
          style="margin-right: 4px"
        >{{ primitive.label }} {{ (primitive.score * 100).toFixed(0) }}%</b-tag>
      </p>
    </section>

    <section class="panel">
      <header>
        <h2>叠加流（{{ overlays.length }}）</h2>
        <b-button size="is-small" icon-left="refresh" :loading="busy === 'overlay'" @click="loadOverlays">刷新</b-button>
      </header>

      <div class="toolbar">
        <b-input v-model="overlayForm.app" size="is-small" placeholder="app" />
        <b-input v-model="overlayForm.stream" size="is-small" placeholder="stream" />
        <b-select v-model="overlayForm.analyzer" size="is-small" placeholder="分析器（全部）">
          <option value="">全部</option>
          <option v-for="item in analyzers" :key="item.id" :value="item.id">{{ item.displayName }}</option>
        </b-select>
        <b-input v-model.number="overlayForm.fps" size="is-small" type="number" placeholder="fps（0=自动）" />
        <b-button size="is-small" type="is-primary" icon-left="flash" :loading="busy === 'start'" @click="startOverlay">
          启动叠加
        </b-button>
      </div>

      <div class="table-wrap" style="margin-top: 12px">
        <table class="table is-fullwidth">
          <thead><tr><th>源流</th><th>叠加流</th><th>状态</th><th>尺寸</th><th>帧数</th><th>操作</th></tr></thead>
          <tbody>
            <tr v-for="item in overlays" :key="item.overlayStream">
              <td class="mono">{{ item.app }}/{{ item.stream }}</td>
              <td class="mono">{{ item.overlayStream }}</td>
              <td><b-tag size="is-small" type="is-success is-light">{{ item.state }}</b-tag></td>
              <td>{{ item.width }}x{{ item.height }}</td>
              <td>{{ item.frames }}</td>
              <td class="actions">
                <b-button
                  size="is-small"
                  icon-left="play"
                  @click="playing = { app: item.app, stream: item.overlayStream }"
                >播放</b-button>
                <b-button
                  size="is-small"
                  type="is-danger is-light"
                  :loading="busy === `stop-${item.overlayStream}`"
                  @click="stopOverlay(item)"
                >停止</b-button>
              </td>
            </tr>
            <tr v-if="overlays.length === 0"><td colspan="6" class="empty">没有叠加流</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <b-modal v-model="playActive" has-modal-card :can-cancel="true">
      <div class="modal-card player-modal" style="width: auto; max-width: 960px">
        <header class="modal-card-head">
          <p class="modal-card-title">{{ playing ? `${playing.app}/${playing.stream}` : '' }}</p>
        </header>
        <section class="modal-card-body">
          <FlvPlayer v-if="playing" :app="playing.app" :stream="playing.stream" />
        </section>
      </div>
    </b-modal>
  </div>
</template>
