<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { call } from '../api'
import type { PushState } from '../types'

interface ProxyRow {
  key: string
  app: string
  stream: string
  url: string
}

// 后端没有“拉流代理列表”接口，只能记录本次会话添加过的 key
const proxies = ref<ProxyRow[]>([])
const pushes = ref<PushState[]>([])
const busy = ref('')

const proxyForm = reactive({ app: 'live', stream: '', url: '' })
const pushForm = reactive({ app: 'live', stream: '', url: '' })

async function addProxy() {
  if (!proxyForm.stream.trim() || !proxyForm.url.trim()) return
  busy.value = 'addProxy'
  try {
    const data = await call<{ key: string }>('addStreamProxy', {
      app: proxyForm.app,
      stream: proxyForm.stream,
      url: proxyForm.url,
    })
    proxies.value.push({ key: data.key, app: proxyForm.app, stream: proxyForm.stream, url: proxyForm.url })
    proxyForm.stream = ''
    proxyForm.url = ''
  } catch {
    // 失败详情已写入日志
  } finally {
    busy.value = ''
  }
}

async function removeProxy(key: string) {
  busy.value = key
  try {
    await call('delStreamProxy', { key })
    proxies.value = proxies.value.filter((item) => item.key !== key)
  } catch {
    // 同上
  } finally {
    busy.value = ''
  }
}

async function loadPushes() {
  try {
    pushes.value = await call<PushState[]>('getPushList')
  } catch {
    // 同上
  }
}

async function addPush() {
  if (!pushForm.stream.trim() || !pushForm.url.trim()) return
  busy.value = 'addPush'
  try {
    await call('startPush', { app: pushForm.app, stream: pushForm.stream, url: pushForm.url })
    pushForm.stream = ''
    pushForm.url = ''
    await loadPushes()
  } catch {
    // 同上
  } finally {
    busy.value = ''
  }
}

async function stopPush(item: PushState) {
  busy.value = item.url
  try {
    await call('stopPush', { app: item.app, stream: item.stream })
    await loadPushes()
  } catch {
    // 同上
  } finally {
    busy.value = ''
  }
}

onMounted(loadPushes)
</script>

<template>
  <div>
    <section class="panel">
      <header><h2>拉流代理（RTSP / RTMP → 本地流）</h2></header>
      <div class="toolbar">
        <b-input v-model="proxyForm.app" size="is-small" placeholder="app" />
        <b-input v-model="proxyForm.stream" size="is-small" placeholder="stream" />
        <b-input
          v-model="proxyForm.url"
          size="is-small"
          expanded
          placeholder="rtsp://user:pass@host/stream 或 rtmp://..."
          @keyup.enter="addProxy"
        />
        <b-button size="is-small" type="is-primary" icon-left="plus" :loading="busy === 'addProxy'" @click="addProxy">
          添加
        </b-button>
      </div>

      <div class="table-wrap" style="margin-top: 12px">
        <table class="table is-fullwidth">
          <thead><tr><th>key</th><th>流</th><th>源地址</th><th>操作</th></tr></thead>
          <tbody>
            <tr v-for="item in proxies" :key="item.key">
              <td class="mono">{{ item.key }}</td>
              <td class="mono">{{ item.app }}/{{ item.stream }}</td>
              <td class="mono">{{ item.url }}</td>
              <td class="actions">
                <b-button size="is-small" type="is-danger is-light" :loading="busy === item.key" @click="removeProxy(item.key)">
                  删除
                </b-button>
              </td>
            </tr>
            <tr v-if="proxies.length === 0">
              <td colspan="4" class="empty">本次会话还没有添加代理（刷新页面后列表不保留）</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <section class="panel">
      <header>
        <h2>RTMP 转推</h2>
        <b-button size="is-small" icon-left="refresh" @click="loadPushes">刷新列表</b-button>
      </header>

      <div class="toolbar">
        <b-input v-model="pushForm.app" size="is-small" placeholder="app" />
        <b-input v-model="pushForm.stream" size="is-small" placeholder="stream" />
        <b-input
          v-model="pushForm.url"
          size="is-small"
          expanded
          placeholder="rtmp://target-host/app/stream"
          @keyup.enter="addPush"
        />
        <b-button size="is-small" type="is-primary" icon-left="broadcast" :loading="busy === 'addPush'" @click="addPush">
          开始转推
        </b-button>
      </div>

      <div class="table-wrap" style="margin-top: 12px">
        <table class="table is-fullwidth">
          <thead><tr><th>流</th><th>目标</th><th>状态</th><th>帧数</th><th>运行时长</th><th>操作</th></tr></thead>
          <tbody>
            <tr v-for="item in pushes" :key="`${item.app}/${item.stream}/${item.url}`">
              <td class="mono">{{ item.app }}/{{ item.stream }}</td>
              <td class="mono">{{ item.url }}</td>
              <td>
                <b-tag size="is-small" :type="item.state === 'pushing' ? 'is-success is-light' : 'is-warning is-light'">
                  {{ item.state }}
                </b-tag>
                <span v-if="item.error" class="muted"> {{ item.error }}</span>
              </td>
              <td>{{ item.frames }}</td>
              <td>{{ Math.round(item.elapsedMs / 1000) }}s</td>
              <td class="actions">
                <b-button size="is-small" type="is-danger is-light" :loading="busy === item.url" @click="stopPush(item)">
                  停止
                </b-button>
              </td>
            </tr>
            <tr v-if="pushes.length === 0"><td colspan="6" class="empty">没有转推任务</td></tr>
          </tbody>
        </table>
      </div>
    </section>
  </div>
</template>
