<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import mpegts from 'mpegts.js'
import { flvUrl } from '../api'

const props = defineProps<{ app: string; stream: string }>()

const video = ref<HTMLVideoElement | null>(null)
const message = ref('')
const url = ref('')

let player: any = null

function destroy() {
  if (!player) return
  try {
    player.pause()
    player.unload()
    player.detachMediaElement()
    player.destroy()
  } catch {
    // 播放器已被卸载时忽略
  }
  player = null
}

function start() {
  destroy()
  message.value = ''
  url.value = flvUrl(props.app, props.stream)

  if (!mpegts.isSupported()) {
    message.value = '当前浏览器不支持 MSE（mpegts.js），无法播放 HTTP-FLV'
    return
  }

  player = mpegts.createPlayer(
    { type: 'flv', url: url.value, isLive: true },
    { enableStashBuffer: false, liveBufferLatencyChasing: true },
  )
  player.on(mpegts.Events.ERROR, (type: string, detail: string) => {
    message.value = `播放失败：${type} ${detail}`
  })

  if (video.value) {
    player.attachMediaElement(video.value)
    player.load()
    player.play().catch(() => {
      // 浏览器自动播放策略：静音后一般可自动播放，失败时等用户点播放
    })
  }
}

watch(() => [props.app, props.stream], start)
onMounted(start)
onBeforeUnmount(destroy)

defineExpose({ destroy })
</script>

<template>
  <div class="flv-player">
    <video ref="video" controls autoplay muted playsinline></video>
    <p class="hint">
      <template v-if="message">{{ message }}</template>
      <template v-else>HTTP-FLV：{{ url }}</template>
    </p>
  </div>
</template>
