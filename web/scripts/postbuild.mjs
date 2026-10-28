// 构建后处理：把 dist/index.html 复制回后端的 WebUI 目录，并预生成 .br / .gz。
// 后端 AdminPageEndpoints 会按 Accept-Encoding 直接吐预压缩文件，省掉运行期压缩的 CPU 开销。
import { brotliCompressSync, gzipSync, constants as zlibConstants } from 'node:zlib'
import { mkdirSync, readFileSync, writeFileSync, statSync } from 'node:fs'
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
writeFileSync(target, html)

const brotli = brotliCompressSync(html, {
  params: {
    [zlibConstants.BROTLI_PARAM_QUALITY]: zlibConstants.BROTLI_MAX_QUALITY,
    [zlibConstants.BROTLI_PARAM_SIZE_HINT]: html.length,
  },
})
const gzip = gzipSync(html, { level: zlibConstants.Z_BEST_COMPRESSION })

writeFileSync(`${target}.br`, brotli)
writeFileSync(`${target}.gz`, gzip)

console.log(`[postbuild] index.html  ${kb(statSync(target).size)}`)
console.log(`[postbuild]  brotli      ${kb(statSync(`${target}.br`).size)}`)
console.log(`[postbuild]  gzip        ${kb(statSync(`${target}.gz`).size)}`)
console.log(`[postbuild] 输出目录     ${targetDir}`)
