<script setup lang="ts">
import { computed } from 'vue'
import { resolveIcon } from '../icons'

// 兼容 Buefy <b-icon> 的 props 约定，从而可以被 defaultIconComponent / 全局注册接管，
// 连 b-table 排序箭头、b-modal 关闭按钮这类“内部图标”也一并走 SVG，无需任何图标字体。
const props = defineProps<{
  // 兼容字符串与 Buefy 传入的 [pack, name] 数组
  icon?: string | (string | null | undefined)[] | null
  pack?: string
  size?: string
  type?: string
  customClass?: string
  customSize?: string
  both?: boolean
  src?: string
}>()

defineOptions({ inheritAttrs: false })

const path = computed(() => resolveIcon(props.icon))

const pixels = computed(() => {
  switch (props.size) {
    case 'small':
      return 16
    case 'medium':
      return 24
    case 'large':
      return 32
    default:
      return 20
  }
})

const classes = computed(() => {
  const list = ['icon']
  if (props.size) list.push(`is-${props.size}`)
  if (props.type) list.push(`has-text-${props.type}`)
  if (props.customClass) list.push(props.customClass)
  return list
})
</script>

<template>
  <span :class="classes">
    <slot v-if="!icon" />
    <svg v-else-if="path" :width="pixels" :height="pixels" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path :d="path" fill="currentColor" />
    </svg>
    <svg v-else :width="pixels" :height="pixels" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <!-- 未在 icons.ts 登记的图标：画个虚线圈，方便一眼看出漏配 -->
      <circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" stroke-width="1.5" stroke-dasharray="3 3" />
    </svg>
  </span>
</template>
