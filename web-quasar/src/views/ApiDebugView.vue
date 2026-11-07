<template>
  <div class="column q-gutter-y-md">
    <div class="panel">
      <div class="panel-head">
        <q-icon name="api" size="18px" class="muted" />
        <div class="panel-title">API 调试</div>
        <q-space />
        <q-select
          v-model="selected"
          :options="apis"
          dense
          outlined
          use-input
          fill-input
          hide-selected
          input-debounce="0"
          options-dense
          label="接口"
          class="api-select"
          @filter="filterApis"
        />
        <q-btn unelevated dense no-caps color="primary" icon="flash" label="发送" :loading="loading" @click="send" />
      </div>
      <q-separator />

      <div class="q-pa-md">
        <div class="row items-center no-wrap q-mb-sm">
          <q-icon name="lan-connect" size="16px" class="muted q-mr-xs" />
          <span class="mono muted ellipsis">{{ preview }}</span>
        </div>

        <div v-for="(item, index) in params" :key="index" class="row q-col-gutter-sm items-center q-mb-xs">
          <div class="col-12 col-sm-4">
            <q-input v-model="item.key" dense outlined placeholder="参数名" class="mono" />
          </div>
          <div class="col-12 col-sm">
            <q-input v-model="item.value" dense outlined placeholder="值" class="mono" @keyup.enter="send" />
          </div>
          <div class="col-auto">
            <q-btn flat dense round size="sm" icon="delete" color="negative" @click="removeParam(index)">
              <q-tooltip>删除这行</q-tooltip>
            </q-btn>
          </div>
        </div>

        <q-btn flat dense no-caps size="sm" icon="plus" label="添加参数" color="primary" class="q-mt-xs" @click="addParam" />
      </div>
    </div>

    <div v-if="result" class="panel">
      <div class="panel-head">
        <q-icon name="content-copy" size="18px" class="muted" />
        <div class="panel-title">响应</div>
        <q-space />
        <q-btn flat dense no-caps size="sm" icon="content-copy" label="复制" @click="copy" />
      </div>
      <q-separator />
      <div class="q-pa-md">
        <pre class="json">{{ result }}</pre>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { Notify } from 'quasar'
import { apiUrl, call, callList } from '../api'

const apis = ref<string[]>([])
const allApis = ref<string[]>([])
const selected = ref('getMediaList')
const params = reactive<{ key: string; value: string }[]>([{ key: '', value: '' }])
const result = ref('')
const loading = ref(false)

const query = computed(() => {
  const map: Record<string, string> = {}
  for (const item of params) {
    if (item.key.trim()) map[item.key.trim()] = item.value
  }
  return map
})

const preview = computed(() => apiUrl(selected.value, query.value))

function filterApis(value: string, update: (fn: () => void) => void) {
  update(() => {
    const kw = value.toLowerCase()
    apis.value = kw ? allApis.value.filter((name) => name.toLowerCase().includes(kw)) : allApis.value
  })
}

onMounted(async () => {
  try {
    const list = await callList<string>('getApiList')
    allApis.value = list.map((path) => path.replace('/index/api/', ''))
    apis.value = allApis.value
    if (apis.value.length > 0 && !apis.value.includes(selected.value)) {
      selected.value = apis.value[0]
    }
  } catch {
    // 失败详情已写入请求日志
  }
})

function addParam() {
  params.push({ key: '', value: '' })
}

function removeParam(index: number) {
  if (params.length === 1) {
    params[0].key = ''
    params[0].value = ''
    return
  }
  params.splice(index, 1)
}

async function send() {
  loading.value = true
  try {
    result.value = JSON.stringify(await call(selected.value, query.value), null, 2)
  } catch (error) {
    result.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }
}

async function copy() {
  try {
    await navigator.clipboard.writeText(result.value)
    Notify.create({ type: 'positive', message: '已复制到剪贴板', timeout: 1500 })
  } catch {
    Notify.create({ type: 'negative', message: '复制失败，请手动选择文本', timeout: 2500 })
  }
}
</script>
