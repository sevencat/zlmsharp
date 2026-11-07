<template>
  <div class="column q-gutter-y-md">
    <q-banner v-if="state.mediaError" dense rounded class="bg-negative text-white">
      <template #avatar>
        <q-icon name="alert" />
      </template>
      {{ state.mediaError }}
    </q-banner>

    <div class="stat-grid">
      <div v-for="card in cards" :key="card.label" class="panel stat">
        <div class="stat-icon" :class="card.tone">
          <q-icon :name="card.icon" size="22px" />
        </div>
        <div>
          <div class="stat-value">{{ card.value }}</div>
          <div class="stat-label">{{ card.label }}</div>
        </div>
      </div>
    </div>

    <div class="panel">
      <div class="panel-head">
        <q-icon name="view-grid" size="18px" class="muted" />
        <div class="panel-title">快捷入口</div>
        <q-space />
        <span class="muted text-caption">{{ navItems.length }} 个功能页</span>
      </div>
      <q-separator />
      <div class="entry-grid">
        <q-btn
          v-for="item in navItems"
          :key="item.path"
          :to="item.path"
          class="entry"
          flat
          no-caps
          :disable="item.path === '/dashboard'"
        >
          <q-icon :name="item.icon" size="22px" class="entry-icon" />
          <div class="entry-text">
            <div class="entry-title">{{ item.label }}</div>
            <div class="entry-desc">{{ item.caption }}</div>
          </div>
        </q-btn>
      </div>
    </div>

    <div class="row q-col-gutter-md">
      <div class="col-12 col-lg-7">
        <div class="panel">
          <div class="panel-head">
            <q-icon name="video" size="18px" class="muted" />
            <div class="panel-title">读者最多的流</div>
            <q-space />
            <q-btn dense flat no-caps size="sm" color="primary" label="查看全部" to="/streams" />
          </div>
          <q-separator />
          <q-table
            :rows="top"
            :columns="columns"
            row-key="key"
            flat
            dense
            :rows-per-page-options="[0]"
            hide-bottom
            no-data-label="暂无在线流"
            class="q-px-sm q-pb-sm"
          >
            <template #body-cell-stream="cell">
              <q-td :props="cell" class="mono">{{ cell.row.key }}</q-td>
            </template>
            <template #body-cell-codec="cell">
              <q-td :props="cell">
                {{ cell.row.videoCodec || '-' }}
                <span v-if="cell.row.audioCodec" class="muted">/ {{ cell.row.audioCodec }}</span>
              </q-td>
            </template>
            <template #body-cell-resolution="cell">
              <q-td :props="cell">{{ resolution(cell.row) }}</q-td>
            </template>
            <template #body-cell-readers="cell">
              <q-td :props="cell">
                <q-badge :color="cell.row.readerCount > 0 ? 'primary' : 'grey-5'" :label="String(cell.row.readerCount)" />
              </q-td>
            </template>
            <template #body-cell-alive="cell">
              <q-td :props="cell">{{ seconds(cell.row.aliveSecond) }}</q-td>
            </template>
          </q-table>
        </div>
      </div>

      <div class="col-12 col-lg-5">
        <div class="panel">
          <div class="panel-head">
            <q-icon name="information-outline" size="18px" class="muted" />
            <div class="panel-title">运行信息</div>
          </div>
          <q-separator />
          <q-list dense class="q-py-sm">
            <q-item v-for="row in infoRows" :key="row.label">
              <q-item-section class="muted" style="max-width: 110px">{{ row.label }}</q-item-section>
              <q-item-section class="mono ellipsis">{{ row.value }}</q-item-section>
            </q-item>
          </q-list>
        </div>

        <div class="panel q-mt-md">
          <div class="panel-head">
            <q-icon name="cog" size="18px" class="muted" />
            <div class="panel-title">任务概览</div>
          </div>
          <q-separator />
          <div class="row q-col-gutter-sm q-pa-md">
            <div class="col-6">
              <div class="task-chip">
                <q-icon name="record" size="18px" class="text-negative" />
                <div>
                  <div class="text-subtitle2">{{ recordingCount }}</div>
                  <div class="stat-label">录像任务</div>
                </div>
              </div>
            </div>
            <div class="col-6">
              <div class="task-chip">
                <q-icon name="robot" size="18px" class="text-primary" />
                <div>
                  <div class="text-subtitle2">{{ state.overlays.length }}</div>
                  <div class="stat-label">叠加会话</div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { QTableColumn } from 'quasar'
import { navItems } from '../router'
import { seconds, state } from '../store'
import type { MediaInfo } from '../types'

const totals = computed(() => ({
  streams: state.media.length,
  readers: state.media.reduce((sum, item) => sum + (item.readerCount || 0), 0),
  drops: state.media.reduce((sum, item) => sum + (item.dropFrames || 0), 0),
  origins: new Set(state.media.map((item) => item.originType || 'unknown')).size,
}))

const recordingCount = computed(
  () => state.recordings.filter((item) => item.state === 'recording' || item.state === 'starting').length,
)

const cards = computed(() => [
  { label: '在线流', value: totals.value.streams, icon: 'video', tone: 'tone-blue' },
  { label: '观看者', value: totals.value.readers, icon: 'account-multiple', tone: 'tone-green' },
  { label: '累计丢帧', value: totals.value.drops, icon: 'alert-circle', tone: 'tone-amber' },
  { label: '来源类型', value: totals.value.origins, icon: 'lan-connect', tone: 'tone-violet' },
])

const top = computed(() =>
  [...state.media]
    .sort((a, b) => (b.readerCount || 0) - (a.readerCount || 0))
    .slice(0, 8)
    .map((item) => ({ ...item, key: `${item.vhost}/${item.app}/${item.stream}` })),
)

const columns: QTableColumn[] = [
  { name: 'stream', label: '流', field: 'key', align: 'left' },
  { name: 'codec', label: '编码', field: 'videoCodec', align: 'left' },
  { name: 'resolution', label: '分辨率', field: 'videoWidth', align: 'left' },
  { name: 'readers', label: '读者', field: 'readerCount', align: 'left' },
  { name: 'alive', label: '存活', field: 'aliveSecond', align: 'left' },
]

const resolution = (item: MediaInfo) => (item.videoWidth ? `${item.videoWidth}x${item.videoHeight}` : '-')

const infoRows = computed(() => [
  { label: '构建时间', value: state.version?.buildTime ?? '-' },
  { label: '分支 / 提交', value: state.version ? `${state.version.branchName} / ${state.version.commitHash}` : '-' },
  { label: '最近刷新', value: state.updatedAt ? new Date(state.updatedAt).toLocaleString() : '-' },
  { label: '页面版本', value: 'Vue 3 + Quasar（单文件内联，图标为内联 SVG）' },
])
</script>
