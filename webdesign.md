# 管理页面（WebUI）设计说明

本文记录 zlmsharp 内置管理页面的技术选型、承载方式、构建流程与约束。目标是**一个自包含的 `index.html`**：不依赖外网 CDN、不加载任何图标字体、不需要在服务端跑 Node。

---

## 1. 总体结论

| 决策点 | 选择 | 原因 |
|---|---|---|
| 产物形态 | 单文件 html（JS/CSS/图标/播放器全部内联） | 离线可用；替换/回滚只是换一个文件 |
| 承载方式 | 外部目录（`http.rootPath`，默认 `./WebUI`），**不内嵌进程序集** | 改页面不用重编译；发布包只多一个文件 |
| 前端框架 | Vue 3 + Buefy 3 | Buefy 3 是原生 Vue 3 版本，自带 Bulma 1.x 样式 |
| 图标 | `@mdi/js`（纯 SVG path 数据） | 不引入 woff2 字体，体积可控且可 tree-shake |
| 播放 | `mpegts.js` 打包进产物 | 旧版从 CDN 引入，离线环境必坏 |
| 构建 | Vite 6 + `vite-plugin-singlefile` 2.1 | 官方做法，`assetsInlineLimit: Infinity` 保证零外链 |
| 预压缩 | 构建时生成 `.br` / `.gz` | 运行期零压缩 CPU，响应体积从 1.5MB 降到 ~216KB |

---

## 2. 目录结构

```
web/                              # 独立前端工程（只在改 UI 时才需要 Node）
  package.json
  vite.config.ts                  # 单文件内联 + dev 代理
  index.html                      # Vite 入口模板（构建后被内联覆盖）
  scripts/postbuild.mjs           # 复制产物到后端 + 预压缩
  src/
    main.ts                       # 挂载 Buefy、接管图标组件、路由
    router.ts                     # hash 路由 + 导航表
    api.ts                        # /index/api/* 统一客户端、secret、请求日志
    store.ts                      # 版本 / 在线流状态与 5s 轮询
    icons.ts                      # @mdi/js 图标映射（显式导入，便于 tree-shake）
    types.ts                      # 与 ApiModels.cs 对应的类型（camelCase）
    App.vue                       # 侧边栏 + 顶栏 + 日志抽屉
    components/
      AppIcon.vue                 # 用 SVG 渲染图标，接管 Buefy 的 <b-icon>
      FlvPlayer.vue               # HTTP-FLV 播放（mpegts.js），卸载即 destroy
    views/
      DashboardView.vue  StreamsView.vue  SessionsView.vue
      RecordsView.vue    RoutesView.vue   AiView.vue   ApiDebugView.vue
    styles/app.css                # 控制台布局与主题
scripts/
  build-web.ps1                   # 一键：装依赖 → 构建 → 回写后端 + 预压缩
  dev-web.ps1                     # 开发服务器（代理 /index/api、/live 到后端）
src/ZlmSharp/WebUI/
  index.html  index.html.br  index.html.gz    # 构建产物，随仓库入库
```

`web/dist`、`web/node_modules` 已在 `web/.gitignore` 中排除；**入库的是 `src/ZlmSharp/WebUI/index.html`**，因此普通开发和使用者不需要安装 Node。

---

## 3. 承载链路

```
web/src/**  ──vite build──▶  web/dist/index.html（单文件）
                                   │
                     postbuild.mjs │ + brotli/gzip
                                   ▼
                 src/ZlmSharp/WebUI/index.html(.br/.gz)
                                   │
      ZlmSharp.csproj 标为 Content（库项目自身输出用）
                                   │
      ⚠ 库项目的 Content 不会传递给引用方，故：
        src/ZlmSharp.Cli 与 samples/ZlmSharp.Sample.Web 各自
        显式 Include 同名文件并 Link 到 WebUI/
                                   ▼
                 <输出目录>/WebUI/index.html(.br/.gz)
                                   │
              AdminPageEndpoints 读磁盘 + 内容协商
                                   ▼
                    浏览器 GET /  →  304 或 200
```

> 新增宿主项目时，若要把管理页面一起发布，需照抄 CLI 项目的那三条 `Content Include`（`Link="WebUI\..."`），否则 `GET /` 会 404。

### 3.1 后端路由

`AdminPageEndpoints` 只占三个路径，不与 `/index/api/*`、`.live.flv` 的 catch-all 冲突：

| 路径 | 行为 |
|---|---|
| `GET /` | 返回 `index.html` |
| `GET /admin` | 同上（书签兼容） |
| `GET /admin/index.html` | 同上 |
| `GET /admin/其它` | 404 纯文本 |

### 3.2 响应策略

1. **根目录解析**：`http.rootPath` 为相对路径时，以 `AppContext.BaseDirectory` 为基准（不是进程 CWD），避免作为 Windows 服务/计划任务启动时去 `System32` 找 `./WebUI`。
   兼容旧部署：若 `rootPath` 仍是**旧默认值 `./www`** 且该目录下没有页面，会回退到 `<程序目录>/WebUI/index.html`（只有这一个旧值会触发回退，用户显式指定的其它目录不做隐式兜底）。
2. **预压缩协商**：按 `Accept-Encoding` 依次尝试 `index.html.br` → `index.html.gz` → `index.html`，命中则加 `Content-Encoding` 与 `Vary: Accept-Encoding`。
3. **缓存**：`Cache-Control: no-cache` + `ETag`（长度 + 修改时间 + 编码后缀），带 `If-None-Match` 时直接 `304`，重复打开页面只传几百字节。
4. **传输**：`Response.SendFileAsync`，不把整页读进内存（旧实现是每次 `ReadAllBytes`）。
5. **缺失提示**：文件不存在时返回 404 纯文本，写明期望的绝对路径，便于排错。

---

## 4. 前端约定

### 4.1 必须用 hash 路由
产物部署在 `/`，后端没有任何 SPA 回退规则，`createWebHashHistory()` 是唯一不 404 的选择。

### 4.2 图标（Buefy 不内置字体）

- `@mdi/js` 提供的是 SVG `path` 字符串，`icons.ts` 里显式 import 用到的图标，`resolveIcon()` 负责归一化名字（支持 `'play'`、`'mdi-play'`、`'fa-play'`，以及 Buefy 传下来的 `[pack, name]` 数组）。
- 两个组件分工：
  - `components/MdiIcon.vue`（全局注册为 **`ZlmIcon`**，根元素是 `<svg>`）：交给 `defaultIconComponent` 使用，接管**所有** Buefy 组件内部图标（表格排序箭头、弹窗关闭叉、分页箭头、密码明示按钮等）。
  - `components/AppIcon.vue`（根元素是 `<span class="icon">`）：我们自己的页面模板里用，可带 slot。
- 配置方式：

  ```ts
  app.component('ZlmIcon', MdiIcon)
  app.use(Buefy, { defaultIconPack: 'mdi', defaultIconComponent: 'ZlmIcon' })
  ```

- **两个必须避开的坑**（都踩过）：
  1. `defaultIconComponent` 的类型是**组件名字符串**，不是组件对象；
  2. 这个字符串**绝不能是 `'BIcon'`**。Buefy 的 `BIcon` 内部逻辑是
     `useIconComponent = component || config.defaultIconComponent`，
     有值时用 `resolveDynamicComponent(useIconComponent)` 渲染该组件 —— 指向 `BIcon` 就是自己渲染自己，页面首屏直接 `RangeError: Maximum call stack size exceeded`。
     也不要再用 `app.component('BIcon', ...)` 去覆盖 Buefy 的组件。
- 自定义组件收到的 props 形态：`icon` 是 `['mdi', 'mdi-play']`（数组），`size` 是 `'mdi-24px'` / `'mdi-36px'` / `'mdi-48px'` 或 `null`，颜色通过外层 `span.icon` 的 `has-text-*` 类经 `currentColor` 继承。
- 未在 `icons.ts` 登记的图标名会渲染成虚线圈，便于一眼发现漏配。新增图标：在 `icons.ts` 里 import 并加入映射表即可。

### 4.3 鉴权
- 后端 `AuthorizeAsync` **只从 query 读 `secret`**，不支持自定义 header；回环地址（127.0.0.1/::1）免校验。
- 前端存在 `localStorage['zlmsharp.secret']`，`api.ts` 自动拼到每个请求。
- 跨机访问且未配置 secret 时，所有 API 返回 401，页面顶栏会显示错误。

### 4.4 播放
- 地址：`/{app}/{stream}.live.flv`（同源，无需 CORS；`http.allow_origins` 默认 `*`）。
- WS-FLV：`ws(s)://{host}/{app}/{stream}.live.flv`，`api.ts` 里的 `wsFlvUrl()` 可直接换用。
- 关闭弹窗即 `destroy()`，避免后台继续拉流占用带宽。
- HEVC 源在浏览器侧无法用 mpegts.js 播放（与封装头无关），这是既有结论。

### 4.5 表格与组件
- 表格统一用原生 `<table class="table is-fullwidth is-hoverable">`（Bulma 类），原因是 Buefy 3 的 `b-table` 插槽 API 与 2.x 有差异，锁死组件反而增加升级成本。
- 交互控件（`b-button` / `b-input` / `b-field` / `b-select` / `b-tag` / `b-modal` / `b-notification`）全部用 Buefy，保留其视觉与无障碍行为。

### 4.6 Buefy 3 与 2.x 的 API 差异（踩过的坑）

网上大量示例是 Buefy 2.x（Vue 2），直接照抄会「按钮点了没反应」：

| 场景 | Buefy 2.x（错） | Buefy 3.x（对） |
|---|---|---|
| 弹窗开关 | `<b-modal v-model:active="visible">` | `<b-modal v-model="visible">`（`modelValue`） |
| 聚焦陷阱 | `trap-focus` prop | `v-trap-focus` **指令**，不是 prop |
| 图标接管 | `defaultIconComponent` 传组件对象 | 传**组件名字符串**，且不能是 `BIcon` |

`v-model:active` 在 3.x 里绑到了一个不存在的 prop，`visible = true` 也不会渲染弹窗 —— 表现就是"播放按钮无效"。改 `v-model` 即可。

---

## 5. 构建与开发

| 目的 | 命令 |
|---|---|
| 构建单文件并回写后端 | `pwsh scripts/build-web.ps1` |
| 只跑 Vite 构建（不装依赖） | `pwsh scripts/build-web.ps1 -SkipInstall` |
| 开发服务器（热更新） | `pwsh scripts/dev-web.ps1 -Backend http://127.0.0.1:8080` |
| 类型检查 | `cd web; npm run typecheck` |
| 后端构建/测试 | `dotnet build src/ZlmSharp/ZlmSharp.csproj` / `dotnet test tests/ZlmSharp.Tests` |

开发模式下 Vite 监听 5173，把 `/index/api` 与 `/live`（含 WebSocket）代理到后端，改代码即时生效；确认无误后再跑一次 `build-web.ps1` 回写产物。

> 本机/部分环境配置了 `npm config set omit dev`，会导致 `vite` 装不上。脚本已使用 `npm ci|install --include=dev` 规避。

---

## 6. 体积与性能（实测）

| 指标 | 数值 |
|---|---|
| `index.html`（未压缩） | 1,611,365 B ≈ **1.54 MB** |
| `index.html.br`（质量 11） | 221,923 B ≈ **217 KB** |
| `index.html.gz`（等级 9） | 292,525 B ≈ **286 KB** |
| 构建耗时 | ≈ 2.5 s（55 modules） |

- 首屏传输走 Brotli ≈ 216 KB；二次打开走 `304`，约 0 字节。
- 体积主要来自：Buefy 全量注册（含 Bulma 样式）、`mpegts.js`、Vue runtime。
- 如需瘦身（按需）：
  1. Buefy 改为按需注册（`app.use(BButton)` 等），只引使用的组件；
  2. 不用播放器的页面用动态 `import()` 拆 mpegts.js —— 注意单文件方案要求 `inlineDynamicImports: true`，拆包意义有限，更适合"去掉不需要的功能"；
  3. Bulma 用 `buefy/dist/css/versions/buefy-no-reset.css` 之类变体，避免 reset 重复。

---

## 7. 后端接口使用情况

全部来自 `/index/api/*`（与 ZLM 兼容，返回 `code`/`msg`/`data`）：

| 页面 | 接口 |
|---|---|
| 总览 / 在线流 | `version`、`getMediaList`、`getApiList` |
| 在线流操作 | `startRecord`、`stopRecord`、`startYoloOverlay`、`close_streams` |
| 会话 | `getSessionList`、`kick_session` |
| 录像 | `getMp4RecordFile`、`deleteRecordFile`、`startRecord`、`stopRecord` |
| 代理 / 转推 | `addStreamProxy`、`delStreamProxy`、`getPushList`、`startPush`、`stopPush` |
| AI 分析 | `yoloInfo`、`getYoloResult`、`analyzeResult`、`startYoloOverlay`、`stopYoloOverlay`、`getYoloOverlay` |
| API 调试 | `getApiList` + 任意接口 |

---

## 8. 约束与已知限制

1. **必须随包带 `WebUI/` 目录**：页面不再内嵌进程序集，目录缺失时 `GET /` 返回 404 文本（写明了期望路径）。库项目的 `Content` 不向引用方传递，宿主项目（CLI / Sample / 新宿主）需各自 `Content Include` 并 `Link="WebUI\..."`。
2. **前端构建需要 Node**，但产物入库，日常开发与 CI 不依赖 Node。
3. **没有配置读写接口**（`getServerConfig` / `setServerConfig` 等未实现），因此页面做不了在线改配置；需要在后端补接口。
4. **拉流代理没有列表接口**：页面只记录本次会话添加的 `key`，刷新后列表丢失（停止时也可用 `app/stream` 参数代替 `key`）。
5. **轮询频率** 5 s；状态变化有最长 5 s 延迟。要更实时需要后端加 WS 推送。
6. `data/` 类响应字段为 camelCase（源生成序列化统一配置），前端类型见 `src/types.ts`，改动后端模型时同步更新。

---

## 9. 后续可做

- 配置编辑页（先补 `getServerConfig` / `setServerConfig`）
- 录像文件下载（`api.downloadRoot` 已存在，缺 HTTP 下载路由）
- 用 SSE/WebSocket 推送替代轮询（在线流、会话、AI 结果）
- 暗色主题（Bulma 1.x 支持 CSS 变量，切换成本低）
- 按需注册 Buefy 组件以压缩体积
