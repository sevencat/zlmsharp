<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { call } from '../api'
import { toast, toastError } from '../toast'
import type { ProxyState, PushState } from '../types'

const proxies = ref<ProxyState[]>([])
const pushes = ref<PushState[]>([])
const busy = ref('')
const pushError = ref('')

const proxyForm = reactive({ app: 'live', stream: '', url: '', keep: false })
const pushForm = reactive({ app: 'live', stream: '', url: '' })

async function loadProxies() {
  try {
    proxies.value = await call<ProxyState[]>('getProxyList')
  } catch {
    // 失败详情已写入日志
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
      keep: proxyForm.keep ? 1 : undefined,
    })
    proxyForm.stream = ''
    proxyForm.url = ''
    toast('success', '拉流代理已添加')
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
  try {
    pushes.value = await call<PushState[]>('getPushList')
  } catch {
    // 同上
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
    // 同时写入侧栏日志；这里额外留在表单下方，避免失败看起来“没反应”
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

<template>
  <div>
    <section class="panel">
      <header>
        <h2>拉流代理（RTSP / RTMP → 本地流）</h2>
        <b-button size="is-small" icon-left="refresh" @click="loadProxies">刷新列表</b-button>
      </header>
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
        <b-checkbox v-model="proxyForm.keep" size="is-small">没人看也不回收</b-checkbox>
        <b-button size="is-small" type="is-primary" icon-left="plus" :loading="busy === 'addProxy'" @click="addProxy">
          添加
        </b-button>
      </div>

      <div class="table-wrap" style="margin-top: 12px">
        <table class="table is-fullwidth">
          <thead><tr><th>key</th><th>流</th><th>源地址</th><th>状态</th><th>运行时长</th><th>操作</th></tr></thead>
          <tbody>
            <tr v-for="item in proxies" :key="item.key">
              <td class="mono">{{ item.key }}</td>
              <td class="mono">{{ item.app }}/{{ item.stream }}</td>
              <td class="mono">{{ item.url }}</td>
              <td>
                <b-tag size="is-small" :type="item.online ? 'is-success is-light' : 'is-warning is-light'">
                  {{ item.online ? '在线' : '连接中/重试' }}
                </b-tag>
                <b-tag v-if="item.keepOnNoneReader" size="is-small" type="is-info is-light">常驻</b-tag>
              </td>
              <td>{{ Math.round(item.elapsedMs / 1000) }}s</td>
              <td class="actions">
                <b-button size="is-small" type="is-danger is-light" :loading="busy === item.key" @click="removeProxy(item.key)">
                  删除
                </b-button>
              </td>
            </tr>
            <tr v-if="proxies.length === 0">
              <td colspan="6" class="empty">没有拉流代理</td>
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

      <p v-if="pushError" class="push-error">{{ pushError }}</p>

      <div class="table-wrap" style="margin-top: 12px">
        <table class="table is-fullwidth">
          <thead><tr><th>流</th><th>目标</th><th>状态</th><th>帧数</th><th>运行时长</th><th>操作</th></tr></thead>
          <tbody>
            <tr v-for="item in pushes" :key="`${item.app}/${item.stream}/${item.url}`">
              <td class="mono">{{ item.app }}/{{ item.stream }}</td>
              <td class="mono">{{ item.url }}</td>
              <td>
                <b-tag
                  size="is-small"
                  :type="
                    item.state === 'running'
                      ? 'is-success is-light'
                      : item.state === 'failed'
                        ? 'is-danger is-light'
                        : 'is-warning is-light'
                  "
                >
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
