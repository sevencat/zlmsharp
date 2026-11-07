<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import mpegts from 'mpegts.js'

const props = defineProps<{ url: string }>()

const video = ref<HTMLVideoElement | null>(null)
const message = ref('')
let player: any = null

const isFlv = () => /\.flv$/i.test(props.url)
const isHls = () => /\.m3u8$/i.test(props.url)
const isNative = () => /\.(mp4|m4v|mov)$/i.test(props.url)

function destroy() {
  if (!player) return
  const p = player
  player = null
  try {
    p.pause()
    p.unload()
    p.detachMediaElement()
    p.destroy()
  } catch {
    // 已经卸载时忽略
  }
}

function attach() {
  destroy()
  message.value = ''
  const element = video.value
  if (!element) return

  // 切换录像时清掉上一个原生 src，避免残留播放旧文件
  element.pause()
  element.removeAttribute('src')
  element.load()

  if (isHls()) {
    message.value = 'HLS 录像请用 Safari 或外部播放器（VLC / ffplay）播放'
    return
  }

  if (!isFlv()) {
    if (!isNative()) {
      message.value = '该格式不支持在线播放，请下载后用外部播放器打开'
      return
    }

    element.src = props.url
    return
  }

  if (!mpegts.isSupported()) {
    message.value = '当前浏览器不支持 MSE（mpegts.js），无法在线播放 FLV，请下载后用 VLC 播放'
    return
  }

  // 点播 FLV：isLive=false 才能拖动进度（服务端 /record 支持 Range）
  const p = mpegts.createPlayer(
    { type: 'flv', url: props.url, isLive: false, hasAudio: true, hasVideo: true },
    { enableStashBuffer: true, isLive: false },
  )
  player = p

  p.on(mpegts.Events.ERROR, (type: string, detail: string) => {
    message.value = `播放失败：${type} ${detail}`
  })

  p.attachMediaElement(element)
  p.load()
  Promise.resolve(p.play()).catch(() => {
    // 浏览器自动播放策略限制时，等用户点播放
  })
}

watch(() => props.url, attach)
onMounted(attach)
onBeforeUnmount(destroy)
</script>

<template>
  <div class="flv-player record-player">
    <video ref="video" controls autoplay playsinline></video>
    <p class="hint">{{ message || url }}</p>
  </div>
</template>
