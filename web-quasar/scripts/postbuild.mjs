// 构建后处理：把 dist/index.html 压缩成 .gz 写回后端 WebUI 目录。
// 与 web/（Buefy 版）共用同一个部署位置，因此构建 Quasar 版就等于切换线上页面。
import { gzipSync } from 'node:zlib'
import { mkdirSync, readFileSync, writeFileSync, rmSync, existsSync, statSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const projectRoot = resolve(here, '..')
const repoRoot = resolve(projectRoot, '..')

const source = resolve(projectRoot, 'dist/index.html')
const targetDir = resolve(repoRoot, 'src/ZlmSharp/WebUI')
const target = resolve(targetDir, 'index.html')

const kb = (bytes) => `${(bytes / 1024).toFixed(1)} KB`

let html
try {
  html = readFileSync(source)
} catch {
  console.error(`[postbuild] 找不到构建产物：${source}`)
  process.exit(1)
}

mkdirSync(targetDir, { recursive: true })

// 清理历史遗留产物：旧的未压缩 index.html 与已废弃的 brotli 版本
for (const stale of [target, `${target}.br`]) {
  if (existsSync(stale)) {
    rmSync(stale)
  }
}

const gzip = gzipSync(html, { level: 9 })

writeFileSync(`${target}.gz`, gzip)

console.log(`[postbuild] index.html   ${kb(html.length)}（仅参与压缩，不落盘）`)
console.log(`[postbuild]  gzip        ${kb(statSync(`${target}.gz`).size)}`)
console.log(`[postbuild] 输出目录     ${targetDir}`)
