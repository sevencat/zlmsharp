<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { call } from '../api'
import FlvPlayer from '../components/FlvPlayer.vue'
import type { YoloInfo, YoloOverlayState, YoloSnapshot } from '../types'

const info = ref<YoloInfo | null>(null)
const snapshots = ref<YoloSnapshot[]>([])
const overlays = ref<YoloOverlayState[]>([])
const analysis = ref('')
const busy = ref('')
const stream = ref('')
const playing = ref<{ app: string; stream: string } | null>(null)

const overlayForm = reactive({ app: 'live', stream: '', fps: 0, scale: 2 })

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
  } catch {
    // 失败详情已写入日志
  } finally {
    busy.value = ''
  }
}

const loadInfo = () => guard('info', async () => (info.value = await call<YoloInfo>('yoloInfo')))
const loadSnapshots = () =>
  guard('snapshot', async () => (snapshots.value = await call<YoloSnapshot[]>('getYoloResult', { stream: stream.value })))
const loadOverlays = () =>
  guard('overlay', async () => (overlays.value = await call<YoloOverlayState[]>('getYoloOverlay')))
const loadAnalysis = () =>
  guard('analysis', async () => {
    const data = await call('analyzeResult', { stream: stream.value })
    analysis.value = JSON.stringify(data, null, 2)
  })

const startOverlay = () =>
  guard('start', async () => {
    await call('startYoloOverlay', {
      app: overlayForm.app,
      stream: overlayForm.stream,
      fps: overlayForm.fps || undefined,
      scale: overlayForm.scale,
    })
    await loadOverlays()
  })

const stopOverlay = (item: YoloOverlayState) =>
  guard(`stop-${item.overlayStream}`, async () => {
    await call('stopYoloOverlay', { app: item.app, stream: item.stream })
    await loadOverlays()
  })

onMounted(async () => {
  await loadInfo()
  await loadOverlays()
})
</script>

<template>
  <div>
    <section class="panel">
      <header>
        <h2>YOLO 状态</h2>
        <b-button size="is-small" icon-left="refresh" :loading="busy === 'info'" @click="loadInfo">刷新</b-button>
      </header>

      <div v-if="info" class="card-grid">
        <div class="metric"><div class="value">{{ info.enabled ? '启用' : '未启用' }}</div><div class="label">服务状态</div></div>
        <div class="metric"><div class="value">{{ info.loaded ? '已加载' : '未加载' }}</div><div class="label">模型</div></div>
        <div class="metric"><div class="value">{{ info.classCount }}</div><div class="label">类别数</div></div>
        <div class="metric"><div class="value">{{ info.inputWidth }}x{{ info.inputHeight }}</div><div class="label">输入尺寸</div></div>
      </div>
      <p v-if="info" class="muted mono">model={{ info.model }} · device={{ info.device }} · conf={{ info.confidence }}</p>
      <p v-else class="empty">未获取到模型信息</p>
    </section>

    <section class="panel">
      <header>
        <h2>检测结果</h2>
        <div class="toolbar">
          <b-input v-model="stream" size="is-small" placeholder="stream（留空=全部）" clearable />
          <b-button size="is-small" :loading="busy === 'snapshot'" @click="loadSnapshots">查询 YOLO</b-button>
          <b-button size="is-small" :loading="busy === 'analysis'" @click="loadAnalysis">通用分析</b-button>
        </div>
      </header>

      <div class="table-wrap">
        <table class="table is-fullwidth is-hoverable">
          <thead><tr><th>流</th><th>尺寸</th><th>目标数</th><th>检测耗时</th><th>结果年龄</th><th>错误</th></tr></thead>
          <tbody>
            <tr v-for="item in snapshots" :key="`${item.app}/${item.stream}`">
              <td class="mono">{{ item.app }}/{{ item.stream }}</td>
              <td>{{ item.width }}x{{ item.height }}</td>
              <td>{{ item.count }}</td>
              <td>{{ item.elapsedMs }}ms</td>
              <td>{{ Math.round(item.ageMs / 1000) }}s</td>
              <td class="muted">{{ item.error ?? '-' }}</td>
            </tr>
            <tr v-if="snapshots.length === 0"><td colspan="6" class="empty">暂无检测结果</td></tr>
          </tbody>
        </table>
      </div>

      <p v-if="snapshots[0]?.objects.length" class="muted">
        首个结果的类别：
        <b-tag
          v-for="object in snapshots[0].objects.slice(0, 12)"
          :key="`${object.trackId}-${object.classId}`"
          size="is-small"
          type="is-info is-light"
          style="margin-right: 4px"
        >{{ object.className }} {{ (object.confidence * 100).toFixed(0) }}%</b-tag>
      </p>

      <pre v-if="analysis" class="json">{{ analysis }}</pre>
    </section>

    <section class="panel">
      <header>
        <h2>叠加流（{{ overlays.length }}）</h2>
        <b-button size="is-small" icon-left="refresh" :loading="busy === 'overlay'" @click="loadOverlays">刷新</b-button>
      </header>

      <div class="toolbar">
        <b-input v-model="overlayForm.app" size="is-small" placeholder="app" />
        <b-input v-model="overlayForm.stream" size="is-small" placeholder="stream" />
        <b-input v-model.number="overlayForm.fps" size="is-small" type="number" placeholder="fps（0=自动）" />
        <b-input v-model.number="overlayForm.scale" size="is-small" type="number" placeholder="缩放倍数" />
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
      <div class="modal-card" style="width: auto; max-width: 960px">
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
