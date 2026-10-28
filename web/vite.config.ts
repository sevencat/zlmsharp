import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { viteSingleFile } from 'vite-plugin-singlefile'

/** 后端默认监听地址（见 zlmsharp.ini 的 http.port，缺省 8080） */
const backend = process.env.ZLMSHARP_BACKEND ?? 'http://127.0.0.1:8080'

export default defineConfig({
  // 单文件产物用相对引用，避免部署到子路径时资源 404
  base: './',
  plugins: [vue(), viteSingleFile()],
  build: {
    target: 'es2020',
    cssCodeSplit: false,
    // 图标字体、woff2 之类也全部内联，保证产物只有一个 index.html
    assetsInlineLimit: Infinity,
    chunkSizeWarningLimit: 8192,
    rollupOptions: {
      output: { inlineDynamicImports: true },
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      // REST API
      '/index/api': { target: backend, changeOrigin: false },
      // HTTP-FLV / WS-FLV 拉流（ws: true 支持 WebSocket 升级）
      '/live': { target: backend, changeOrigin: false, ws: true },
    },
  },
})
