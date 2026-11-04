<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { call } from '../api'
import { toast, toastError } from '../toast'
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
/** 正在播放的录像 */
const playing = ref<{ name: string; url: string } | null>(null)

const playActive = computed({
  get: () => playing.value !== null,
  set: (value: boolean) => {
    if (!value) playing.value = null
  },
})

const playRecord = (name: string) => {
  playing.value = { name, url: recordUrl(name) }
}

/** 面包屑：live / cam / 2026-11-04 */
const segments = computed(() => currentPath.value.split('/').filter((item) => item.length > 0))

/** 当前列表是子目录还是文件（由后端 directories 字段告知） */
const isDirectory = () => data.value?.directories === true

const recordUrl = (name: string) =>
  `/record/${currentPath.value.split('/').filter((item) => item.length > 0).map(encodeURIComponent).join('/')}/${encodeURIComponent(name)}`

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

    data.value = await call<RecordFileData>('getMp4RecordFile', params)
    currentPath.value = data.value.rootPath
  } catch (exception) {
    error.value = exception instanceof Error ? exception.message : String(exception)
    data.value = null
  } finally {
    loading.value = false
  }
}

/** 按 app / stream 快速跳到对应目录（也可以直接点目录浏览） */
async function query() {
  if (!app.value.trim() && !stream.value.trim()) {
    error.value = '请填写 app 或 stream，或直接点目录浏览'
    return
  }

  await browse('', true)
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

async function remove(path: string) {
  busy.value = path
  try {
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
    recordings.value = await call<RecordState[]>('getRecordList', { stream: stream.value })
  } catch {
    // 同上
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

<template>
  <div>
    <section class="panel">
      <header>
        <h2>录像查询</h2>
        <div class="toolbar">
          <b-input v-model="app" size="is-small" placeholder="app" />
          <b-input v-model="stream" size="is-small" placeholder="stream" @keyup.enter="query" />
          <b-select v-model="recordFormat" size="is-small">
            <option :value="3">MP4+FLV</option>
            <option :value="2">仅 FLV</option>
            <option :value="1">仅 MP4</option>
            <option :value="4">HLS</option>
          </b-select>
          <b-button size="is-small" type="is-primary" icon-left="magnify" :loading="loading" @click="query">定位</b-button>
          <b-button size="is-small" icon-left="refresh" :loading="loading" @click="browse(currentPath)">刷新</b-button>
          <b-button size="is-small" :loading="busy === 'start'" @click="toggleRecord(true)">开始录像</b-button>
          <b-button size="is-small" :loading="busy === 'stop'" @click="toggleRecord(false)">停止录像</b-button>
        </div>
      </header>

      <b-notification v-if="error" type="is-danger" :closable="false">{{ error }}</b-notification>

      <p v-if="recordings.length" class="muted mono" style="margin-bottom: 8px">
        录制中：
        <b-tag v-for="item in recordings" :key="`${item.app}/${item.stream}`" size="is-small" type="is-success is-light">
          {{ item.app }}/{{ item.stream }} · {{ item.state }} · {{ item.mp4Path ?? item.flvPath ?? '-' }}
        </b-tag>
      </p>

      <template v-if="data">
        <p class="muted mono">
          <a class="link" @click="browse('')">/record</a>
          <template v-for="(segment, index) in segments" :key="`${segment}-${index}`">
            /<a class="link" @click="browse(segments.slice(0, index + 1).join('/'))">{{ segment }}</a>
          </template>
          <b-button v-if="currentPath" size="is-small" icon-left="arrow-left" style="margin-left: 8px" @click="up">上级</b-button>
        </p>
        <div class="table-wrap">
          <table class="table is-fullwidth is-hoverable">
            <thead><tr><th>名称</th><th>操作</th></tr></thead>
            <tbody>
              <tr v-for="name in data.paths" :key="name">
                <td class="mono">
                  <a v-if="isDirectory()" class="link" @click="enter(name)">{{ name }}/</a>
                  <span v-else>{{ name }}</span>
                </td>
                <td class="actions">
                  <b-button
                    v-if="!isDirectory()"
                    size="is-small"
                    type="is-primary is-light"
                    icon-left="play"
                    @click="playRecord(name)"
                  >播放</b-button>
                  <b-button
                    v-if="!isDirectory()"
                    size="is-small"
                    icon-left="download"
                    tag="a"
                    :href="recordUrl(name)"
                    target="_blank"
                  >下载</b-button>
                  <b-button
                    v-if="!isDirectory()"
                    size="is-small"
                    type="is-danger is-light"
                    :loading="busy === name"
                    @click="remove(currentPath ? `${currentPath}/${name}` : name)"
                  >删除</b-button>
                </td>
              </tr>
              <tr v-if="data.paths.length === 0"><td colspan="2" class="empty">没有录像文件</td></tr>
            </tbody>
          </table>
        </div>
      </template>

      <p v-else class="empty">录像目录为空（还没有录制过，或录像根目录未配置）</p>
    </section>

    <b-modal v-model="playActive" has-modal-card :can-cancel="true">
      <div class="modal-card player-modal" style="width: auto; max-width: 960px">
        <header class="modal-card-head">
          <p class="modal-card-title">{{ playing?.name ?? '' }}</p>
          <b-button
            v-if="playing"
            size="is-small"
            icon-left="download"
            tag="a"
            :href="playing.url"
            target="_blank"
          >下载</b-button>
        </header>
        <section class="modal-card-body">
          <RecordPlayer v-if="playing" :url="playing.url" />
        </section>
      </div>
    </b-modal>
  </div>
</template>
