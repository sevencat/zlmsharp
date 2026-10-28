<script setup lang="ts">
import { ref } from 'vue'
import { call } from '../api'
import type { RecordFileData } from '../types'

const app = ref('live')
const stream = ref('')
const loading = ref(false)
const error = ref('')
const data = ref<RecordFileData | null>(null)
const busy = ref('')

async function query() {
  if (!stream.value.trim()) {
    error.value = '请填写 stream'
    return
  }

  loading.value = true
  error.value = ''
  try {
    data.value = await call<RecordFileData>('getMp4RecordFile', { app: app.value, stream: stream.value })
  } catch (exception) {
    error.value = exception instanceof Error ? exception.message : String(exception)
    data.value = null
  } finally {
    loading.value = false
  }
}

async function remove(path: string) {
  busy.value = path
  try {
    await call('deleteRecordFile', { app: app.value, stream: stream.value, path })
    await query()
  } catch {
    // 失败详情已写入日志
  } finally {
    busy.value = ''
  }
}

async function toggleRecord(start: boolean) {
  busy.value = start ? 'start' : 'stop'
  try {
    if (start) {
      await call('startRecord', { app: app.value, stream: stream.value, type: 3 })
    } else {
      await call('stopRecord', { app: app.value, stream: stream.value })
    }
    await query()
  } catch {
    // 同上
  } finally {
    busy.value = ''
  }
}
</script>

<template>
  <div>
    <section class="panel">
      <header>
        <h2>录像查询</h2>
        <div class="toolbar">
          <b-input v-model="app" size="is-small" placeholder="app" />
          <b-input v-model="stream" size="is-small" placeholder="stream" @keyup.enter="query" />
          <b-button size="is-small" type="is-primary" icon-left="magnify" :loading="loading" @click="query">查询</b-button>
          <b-button size="is-small" :loading="busy === 'start'" @click="toggleRecord(true)">开始录像</b-button>
          <b-button size="is-small" :loading="busy === 'stop'" @click="toggleRecord(false)">停止录像</b-button>
        </div>
      </header>

      <b-notification v-if="error" type="is-danger" :closable="false">{{ error }}</b-notification>

      <template v-if="data">
        <p class="muted mono">根目录：{{ data.rootPath }}</p>
        <div class="table-wrap">
          <table class="table is-fullwidth is-hoverable">
            <thead><tr><th>路径</th><th>操作</th></tr></thead>
            <tbody>
              <tr v-for="path in data.paths" :key="path">
                <td class="mono">{{ path }}</td>
                <td class="actions">
                  <b-button
                    size="is-small"
                    type="is-danger is-light"
                    :loading="busy === path"
                    @click="remove(path)"
                  >删除</b-button>
                </td>
              </tr>
              <tr v-if="data.paths.length === 0"><td colspan="2" class="empty">没有录像文件</td></tr>
            </tbody>
          </table>
        </div>
      </template>

      <p v-else class="empty">输入 app / stream 后查询该路流的录像切片</p>
    </section>
  </div>
</template>
