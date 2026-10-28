<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { apiUrl, call } from '../api'

const apis = ref<string[]>([])
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

onMounted(async () => {
  try {
    const list = await call<string[]>('getApiList')
    apis.value = list.map((path) => path.replace('/index/api/', ''))
    if (apis.value.length > 0 && !apis.value.includes(selected.value)) {
      selected.value = apis.value[0]
    }
  } catch {
    // 失败详情已写入日志
  }
})

function addParam() {
  params.push({ key: '', value: '' })
}

function removeParam(index: number) {
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
</script>

<template>
  <div>
    <section class="panel">
      <header>
        <h2>API 调试</h2>
        <div class="toolbar">
          <b-select v-model="selected" size="is-small">
            <option v-for="name in apis" :key="name" :value="name">{{ name }}</option>
          </b-select>
          <b-button size="is-small" type="is-primary" icon-left="flash" :loading="loading" @click="send">发送</b-button>
        </div>
      </header>

      <div class="table-wrap">
        <table class="table is-fullwidth">
          <thead><tr><th style="width: 36%">参数</th><th>值</th><th style="width: 60px"></th></tr></thead>
          <tbody>
            <tr v-for="(item, index) in params" :key="index">
              <td><b-input v-model="item.key" size="is-small" placeholder="名称，如 app" /></td>
              <td><b-input v-model="item.value" size="is-small" placeholder="值" @keyup.enter="send" /></td>
              <td class="actions">
                <b-button size="is-small" type="is-danger is-light" icon-left="close" @click="removeParam(index)" />
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <div class="toolbar" style="margin-top: 8px">
        <b-button size="is-small" icon-left="plus" @click="addParam">添加参数</b-button>
      </div>

      <p class="muted mono" style="margin-top: 10px; word-break: break-all">GET {{ preview }}</p>
    </section>

    <section class="panel">
      <header><h2>响应</h2></header>
      <pre v-if="result" class="json">{{ result }}</pre>
      <p v-else class="empty">还没有请求。选择接口、填参数后点“发送”。</p>
    </section>
  </div>
</template>
