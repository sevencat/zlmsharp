/** 与 src/ZlmSharp/Api/ApiModels.cs 对应的前端类型（JSON 为 camelCase） */

export interface MediaInfo {
  vhost: string
  app: string
  stream: string
  schema: string
  originType: string
  aliveSecond: number
  readerCount: number
  totalReaderCount: number
  videoCodec: string
  videoWidth: number
  videoHeight: number
  videoFps: number
  audioCodec: string
  audioSampleRate: number
  audioChannels: number
  dropFrames: number
}

export interface SessionInfo {
  id: string
  schema: string
  type: string
  vhost: string
  app: string
  stream: string
  ip: string
  port: number
  aliveSecond: number
  userAgent?: string | null
}

export interface RecordState {
  vhost: string
  app: string
  stream: string
  state: string
  mp4: boolean
  flv: boolean
  /** 相对录像根目录的归一化路径（'/' 分隔），拼 /record/ 即可访问 */
  mp4Path?: string | null
  flvPath?: string | null
  hlsPath?: string | null
  frames: number
  durationMs: number
  startedAt: number
  error?: string | null
}

export interface RecordFileData {
  /** 相对录像根目录的目录路径（'/' 分隔） */
  rootPath: string
  /** directories=true 时是子目录名，否则是文件名 */
  paths: string[]
  directories: boolean
}

export interface PushState {
  vhost: string
  app: string
  stream: string
  url: string
  state: string
  frames: number
  startedAt: number
  elapsedMs: number
  error?: string | null
}

export interface ProxyState {
  key: string
  vhost: string
  app: string
  stream: string
  url: string
  online: boolean
  startedAt: number
  elapsedMs: number
  /** 没人看也不回收（常驻） */
  keepOnNoneReader: boolean
  /** 按需拉流：只在有人点播时才拉；还没被触发时 online=false、startedAt=0 */
  onDemand?: boolean
}

export interface AnalyzerInfo {
  id: string
  displayName: string
  enabled: boolean
  ready: boolean
  model: string
  modelDir: string
  device: string
  backend: string
  labels: string[]
  models: string[]
  loadedModels: string[]
  settings: Record<string, string>
  error?: string | null
}

export interface AnalysisPrimitive {
  kind: string
  label: string
  score: number
  trackId?: number | null
  x?: number | null
  y?: number | null
  width?: number | null
  height?: number | null
  points?: { x: number; y: number }[] | null
  text?: string | null
}

export interface AnalysisData {
  analyzer: string
  vhost: string
  app: string
  stream: string
  at: number
  ageMs: number
  elapsedMs: number
  width: number
  height: number
  count: number
  primitives: AnalysisPrimitive[]
  error?: string | null
}

export interface OverlayState {
  vhost: string
  app: string
  stream: string
  overlayStream: string
  input: string
  output: string
  state: string
  width: number
  height: number
  fps: number
  bitrateKbps: number
  frames: number
  startedAt: number
  elapsedMs: number
  error?: string | null
}

export interface VersionInfo {
  buildTime: string
  branchName: string
  commitHash: string
}
