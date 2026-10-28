<script setup lang="ts">
import { computed } from 'vue'
import { resolveIcon } from '../icons'

// 供 Buefy 的 defaultIconComponent 使用（注册名为 ZlmIcon）。
// 注意：Buefy 的 BIcon 会先渲染 <span class="icon">，再用本组件替换内部的 <i>，
// 并且传入的 icon 是 [pack, name] 数组、size 是 'mdi-24px' 这类类名 ——
// 因此这里根元素必须是 <svg>，不要自带 span 容器。
// 另外绝不能把 defaultIconComponent 指回 'BIcon' 自己，否则会无限递归。
const props = defineProps<{
  icon?: string | (string | null | undefined)[] | null
  size?: string | null
  customClass?: string
}>()

const path = computed(() => resolveIcon(props.icon))

// Buefy 的 mdi pack 尺寸类 → 实际像素
const SIZE_MAP: Record<string, number> = {
  'mdi-24px': 20,
  'mdi-36px': 24,
  'mdi-48px': 32,
}

const pixels = computed(() => (props.size ? SIZE_MAP[props.size] ?? 20 : 16))
</script>

<template>
  <svg
    :width="pixels"
    :height="pixels"
    viewBox="0 0 24 24"
    fill="currentColor"
    aria-hidden="true"
    focusable="false"
    :class="customClass"
  >
    <path v-if="path" :d="path" />
    <!-- 未在 icons.ts 登记的图标：画个虚线圈，方便一眼看出漏配 -->
    <circle
      v-else
      cx="12"
      cy="12"
      r="9"
      fill="none"
      stroke="currentColor"
      stroke-width="1.5"
      stroke-dasharray="3 3"
    />
  </svg>
</template>
