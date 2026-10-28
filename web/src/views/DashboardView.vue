<script setup lang="ts">
import { computed } from 'vue'
import { state, streamLabel, seconds } from '../store'

const totals = computed(() => ({
  streams: state.media.length,
  readers: state.media.reduce((sum, item) => sum + (item.readerCount || 0), 0),
  drops: state.media.reduce((sum, item) => sum + (item.dropFrames || 0), 0),
  origins: new Set(state.media.map((item) => item.originType || 'unknown')).size,
}))

const top = computed(() =>
  [...state.media].sort((a, b) => (b.readerCount || 0) - (a.readerCount || 0)).slice(0, 8),
)
</script>

<template>
  <div>
    <b-notification v-if="state.mediaError" type="is-danger" :closable="false">
      {{ state.mediaError }}
    </b-notification>

    <div class="card-grid">
      <div class="metric"><div class="value">{{ totals.streams }}</div><div class="label">在线流</div></div>
      <div class="metric"><div class="value">{{ totals.readers }}</div><div class="label">观看者</div></div>
      <div class="metric"><div class="value">{{ totals.drops }}</div><div class="label">累计丢帧</div></div>
      <div class="metric"><div class="value">{{ totals.origins }}</div><div class="label">来源类型</div></div>
    </div>

    <section class="panel">
      <header>
        <h2>读者最多的流</h2>
        <router-link to="/streams" class="button is-small is-light">查看全部</router-link>
      </header>
      <div class="table-wrap">
        <table class="table is-fullwidth is-hoverable">
          <thead>
            <tr><th>流</th><th>编码</th><th>分辨率</th><th>读者</th><th>存活</th></tr>
          </thead>
          <tbody>
            <tr v-for="item in top" :key="`${item.vhost}/${item.app}/${item.stream}`">
              <td class="mono">{{ streamLabel(item) }}</td>
              <td>{{ item.videoCodec || '-' }}<span v-if="item.audioCodec"> / {{ item.audioCodec }}</span></td>
              <td>{{ item.videoWidth ? `${item.videoWidth}x${item.videoHeight}` : '-' }}</td>
              <td>{{ item.readerCount }}</td>
              <td>{{ seconds(item.aliveSecond) }}</td>
            </tr>
            <tr v-if="top.length === 0"><td colspan="5" class="empty">暂无在线流</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <section class="panel">
      <header><h2>运行信息</h2></header>
      <div class="table-wrap">
        <table class="table is-fullwidth">
          <tbody>
            <tr>
              <td class="muted" style="width: 160px">构建时间</td>
              <td>{{ state.version?.buildTime ?? '-' }}</td>
            </tr>
            <tr>
              <td class="muted">分支 / 提交</td>
              <td>{{ state.version ? `${state.version.branchName} / ${state.version.commitHash}` : '-' }}</td>
            </tr>
            <tr>
              <td class="muted">最近刷新</td>
              <td>{{ state.updatedAt ? new Date(state.updatedAt).toLocaleString() : '-' }}</td>
            </tr>
            <tr>
              <td class="muted">页面版本</td>
              <td>Vue 3 + Buefy（单文件内联，图标为内联 SVG）</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </div>
</template>
