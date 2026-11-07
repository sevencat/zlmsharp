<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import mpegts from 'mpegts.js'
import { flvUrl } from '../api'

const props = defineProps<{ app: string; stream: string }>()

const video = ref<HTMLVideoElement | null>(null)
const message = ref('')
const url = ref('')

// 媒体解码类错误（编码不支持等）重试意义不大，限制次数；网络类错误保持无限重连
const MEDIA_ERROR_MAX_RETRY = 3
const RETRY_BASE_DELAY = 1000
const RETRY_MAX_DELAY = 5000

let player: any = null
let retryTimer: ReturnType<typeof setTimeout> | null = null
let retryCount = 0
let mediaErrorCount = 0
// 组件是否仍然存活，卸载后不再重连
let alive = false

function clearRetry() {
  if (retryTimer !== null) {
    clearTimeout(retryTimer)
    retryTimer = null
  }
}

function destroy() {
  clearRetry()
  if (!player) return
  const p = player
  player = null
  try {
    p.pause()
    p.unload()
    p.detachMediaElement()
    p.destroy()
  } catch {
    // 播放器已被卸载时忽略
  }
}

function scheduleRetry(reason: string) {
  if (!alive) return
  retryCount += 1
  const delay = Math.min(RETRY_BASE_DELAY * 2 ** (retryCount - 1), RETRY_MAX_DELAY)
  message.value = `${reason}，${Math.round(delay / 1000)}s 后重连（第 ${retryCount} 次）`
  clearRetry()
  retryTimer = setTimeout(() => {
    retryTimer = null
    attach()
  }, delay)
}

function attach() {
  if (!alive || !video.value || !url.value) return
  destroy()

  if (!mpegts.isSupported()) {
    message.value = '当前浏览器不支持 MSE（mpegts.js），无法播放 HTTP-FLV'
    return
  }

  const p = mpegts.createPlayer(
    { type: 'flv', url: url.value, isLive: true },
    {
      enableStashBuffer: false, // 不攒缓冲，保证实时
      liveBufferLatencyChasing: true, // 落后时加速/跳帧追上去
      liveBufferLatencyMaxLatency: 1.5, // 超过 1.5s 才开始追
      liveBufferLatencyMinRemain: 0.5, // 追到只剩 0.5s 余量就停
      autoCleanupSourceBuffer: true, // 定期清理已播数据，长时间播放不越积越多
    },
  )
  player = p

  p.on(mpegts.Events.ERROR, (type: string, detail: string) => {
    // 旧实例的迟到回调，忽略
    if (player !== p) return
    console.warn('mpegts error', type, detail, p.mediaInfo)

    if (type === mpegts.ErrorTypes.MEDIA_ERROR) {
      mediaErrorCount += 1
      if (mediaErrorCount > MEDIA_ERROR_MAX_RETRY) {
        destroy()
        message.value = `播放失败：${type} ${detail}（已重试 ${MEDIA_ERROR_MAX_RETRY} 次，可能是编码不受支持）`
        return
      }
    }

    destroy()
    scheduleRetry(`播放中断：${type} ${detail}`)
  })

  // 真正出流后清空错误计数与提示
  p.on(mpegts.Events.MEDIA_INFO, () => {
    retryCount = 0
    mediaErrorCount = 0
    message.value = ''
  })

  p.attachMediaElement(video.value)
  p.load()
  Promise.resolve(p.play()).catch(() => {
    // 浏览器自动播放策略：静音后一般可自动播放，失败时等用户点播放
  })
}

function start() {
  alive = true
  retryCount = 0
  mediaErrorCount = 0
  message.value = ''
  url.value = flvUrl(props.app, props.stream)
  attach()
}

watch(() => [props.app, props.stream], start)
onMounted(start)
onBeforeUnmount(() => {
  alive = false
  destroy()
})

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
