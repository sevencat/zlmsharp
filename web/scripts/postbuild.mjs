// 构建后处理：把 dist/index.html 压缩成 .gz 写回后端 WebUI 目录。
// 后端只把这一个压缩文件内嵌进程序集（ZlmSharp.csproj 的 EmbeddedResource），
// 未压缩的 index.html 不再写入磁盘，发布产物里也不需要 WebUI 目录。
import { gzipSync } from 'node:zlib'
import { mkdirSync, readFileSync, writeFileSync, rmSync, existsSync, statSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const webRoot = resolve(here, '..')
const repoRoot = resolve(webRoot, '..')

const source = resolve(webRoot, 'dist/index.html')
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
