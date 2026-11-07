<#
.SYNOPSIS
    启动管理页面的前端开发服务器（带后端代理）。

.DESCRIPTION
    vite dev server 把 /index/api/* 与 /live/* 代理到后端，
    这样开发时不需要每次都重新打包单文件。
    端口：buefy 版 5173，quasar 版 5174。

.PARAMETER Backend
    后端地址，缺省 http://127.0.0.1:8080（zlmsharp.ini 里 http.port 的缺省值）

.PARAMETER Frontend
    选择前端工程：buefy（web/，默认）或 quasar（web-quasar/）

.EXAMPLE
    pwsh scripts/dev-web.ps1
    pwsh scripts/dev-web.ps1 -Backend http://192.168.1.10:8080
    pwsh scripts/dev-web.ps1 -Frontend quasar
#>
[CmdletBinding()]
param(
    [string]$Backend = 'http://127.0.0.1:8080',
    [ValidateSet('buefy', 'quasar')]
    [string]$Frontend = 'buefy'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ($Frontend -eq 'quasar') {
    $webRoot = Join-Path $repoRoot 'web-quasar'
}
else {
    $webRoot = Join-Path $repoRoot 'web'
}

if (-not (Test-Path $webRoot)) {
    throw "找不到前端工程目录：$webRoot"
}

$env:ZLMSHARP_BACKEND = $Backend

Push-Location $webRoot
try {
    if (-not (Test-Path 'node_modules')) {
        Write-Host '==> 首次运行，安装依赖' -ForegroundColor Cyan
        npm install --include=dev --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) {
            throw "依赖安装失败（exit $LASTEXITCODE）"
        }
    }

    Write-Host "==> vite dev（代理到 $Backend）" -ForegroundColor Cyan
    npm run dev
}
finally {
    Pop-Location
}
