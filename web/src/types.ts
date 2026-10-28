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

export interface RecordFileData {
  rootPath: string
  paths: string[]
}

export interface RecordState {
  vhost: string
  app: string
  stream: string
  state: string
  mp4: boolean
  flv: boolean
  mp4Path?: string | null
  flvPath?: string | null
  frames: number
  durationMs: number
  startedAt: number
  error?: string | null
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

export interface YoloObject {
  classId: number
  className: string
  confidence: number
  x: number
  y: number
  width: number
  height: number
  trackId: number
}

export interface YoloSnapshot {
  vhost: string
  app: string
  stream: string
  at: number
  ageMs: number
  elapsedMs: number
  width: number
  height: number
  count: number
  objects: YoloObject[]
  error?: string | null
}

export interface YoloInfo {
  enabled: boolean
  loaded: boolean
  model: string
  modelDir: string
  device: string
  inputWidth: number
  inputHeight: number
  confidence: number
  classCount: number
  task?: string | null
  classNames: string[]
  models: string[]
  loadedModels: string[]
}

export interface YoloOverlayState {
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
