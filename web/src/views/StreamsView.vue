<script setup lang="ts">
import { computed, ref } from 'vue'
import { call, log } from '../api'
import { refresh, seconds, state, streamLabel } from '../store'
import FlvPlayer from '../components/FlvPlayer.vue'
import type { MediaInfo } from '../types'

const keyword = ref('')
const busy = ref('')
const playing = ref<{ app: string; stream: string } | null>(null)

const playActive = computed({
  get: () => playing.value !== null,
  set: (value: boolean) => {
    if (!value) playing.value = null
  },
})

const rows = computed(() => {
  const kw = keyword.value.trim().toLowerCase()
  if (!kw) return state.media
  return state.media.filter((item) =>
    [item.app, item.stream, item.vhost, item.schema, item.originType, item.videoCodec, item.audioCodec]
      .join(' ')
      .toLowerCase()
      .includes(kw),
  )
})

async function run(label: string, action: () => Promise<unknown>) {
  busy.value = label
  try {
    await action()
    log('info', `${label} 完成`)
    await refresh()
  } catch {
    // 失败详情已由 api 层写入日志
  } finally {
    busy.value = ''
  }
}

const play = (item: MediaInfo) => {
  playing.value = { app: item.app, stream: item.stream }
}

const startRecord = (item: MediaInfo) =>
  run('开始录像', () => call('startRecord', { app: item.app, stream: item.stream, type: 3 }))

const stopRecord = (item: MediaInfo) =>
  run('停止录像', () => call('stopRecord', { app: item.app, stream: item.stream }))

const startOverlay = (item: MediaInfo) =>
  run('YOLO 叠加', () => call('startYoloOverlay', { app: item.app, stream: item.stream }))

const closeStream = (item: MediaInfo) =>
  run('关闭流', () => call('close_streams', { app: item.app, stream: item.stream }))
</script>

<template>
  <div>
    <b-notification v-if="state.mediaError" type="is-danger" :closable="false">
      {{ state.mediaError }}
    </b-notification>

    <section class="panel">
      <header>
        <h2>在线流（{{ rows.length }} / {{ state.media.length }}）</h2>
        <div class="toolbar">
          <b-input
            v-model="keyword"
            size="is-small"
            icon="magnify"
            placeholder="过滤 app / stream / 编码"
            clearable
          />
          <b-button size="is-small" icon-left="refresh" :loading="state.loading" @click="refresh">刷新</b-button>
        </div>
      </header>

      <div class="table-wrap">
        <table class="table is-fullwidth is-hoverable">
          <thead>
            <tr>
              <th>流</th>
              <th>编码</th>
              <th>分辨率</th>
              <th>读者</th>
              <th>来源</th>
              <th>丢帧</th>
              <th>存活</th>
              <th>操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="item in rows" :key="`${item.vhost}/${item.app}/${item.stream}`">
              <td class="mono" :title="`${item.vhost}/${item.app}/${item.stream}`">{{ streamLabel(item) }}</td>
              <td>
                {{ item.videoCodec || '-' }}
                <span v-if="item.audioCodec"> / {{ item.audioCodec }}</span>
              </td>
              <td>
                {{ item.videoWidth ? `${item.videoWidth}x${item.videoHeight}` : '-' }}
                <span v-if="item.videoFps"> @{{ Math.round(item.videoFps) }}</span>
              </td>
              <td>{{ item.readerCount }}</td>
              <td><b-tag size="is-small" type="is-info is-light">{{ item.originType || '-' }}</b-tag></td>
              <td>{{ item.dropFrames }}</td>
              <td>{{ seconds(item.aliveSecond) }}</td>
              <td class="actions">
                <b-button size="is-small" icon-left="play" @click="play(item)">播放</b-button>
                <b-button size="is-small" :loading="busy === '开始录像'" @click="startRecord(item)">录像</b-button>
                <b-button size="is-small" :loading="busy === '停止录像'" @click="stopRecord(item)">停录</b-button>
                <b-button size="is-small" icon-left="robot" :loading="busy === 'YOLO 叠加'" @click="startOverlay(item)">叠加</b-button>
                <b-button size="is-small" type="is-danger is-light" :loading="busy === '关闭流'" @click="closeStream(item)">关闭</b-button>
              </td>
            </tr>
            <tr v-if="rows.length === 0"><td colspan="8" class="empty">没有匹配的在线流</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <b-modal v-model="playActive" has-modal-card :can-cancel="true">
      <div class="modal-card" style="width: auto; max-width: 960px">
        <header class="modal-card-head">
          <p class="modal-card-title">
            {{ playing ? streamLabel(playing) : '' }}
          </p>
        </header>
        <section class="modal-card-body">
          <FlvPlayer v-if="playing" :app="playing.app" :stream="playing.stream" />
        </section>
      </div>
    </b-modal>
  </div>
</template>
