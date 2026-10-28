<#
.SYNOPSIS
    构建管理页面单文件，并回写到后端可部署的位置。

.DESCRIPTION
    1. 在 web/ 里装依赖（有 package-lock.json 用 npm ci，否则 npm install）
    2. vite build + vite-plugin-singlefile → web/dist/index.html（单个自包含 html）
    3. postbuild.mjs 把它复制到 src/ZlmSharp/WebUI/index.html，并预生成 .br / .gz
    4. .NET 构建/发布时，这些文件会被拷到输出目录的 www/ 下（见 ZlmSharp.csproj）

.PARAMETER SkipInstall
    跳过 npm install（依赖已就绪时加快重复构建）

.EXAMPLE
    pwsh scripts/build-web.ps1
    pwsh scripts/build-web.ps1 -SkipInstall
#>
[CmdletBinding()]
param(
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$webRoot = Join-Path $repoRoot 'web'

if (-not (Test-Path $webRoot)) {
    throw "找不到前端工程目录：$webRoot"
}

Push-Location $webRoot
try {
    if (-not $SkipInstall) {
        # --include=dev：部分环境配置了 npm omit=dev，会漏装 vite 等构建依赖
        if (Test-Path 'package-lock.json') {
            Write-Host '==> npm ci' -ForegroundColor Cyan
            npm ci --include=dev --no-audit --no-fund
        }
        else {
            Write-Host '==> npm install' -ForegroundColor Cyan
            npm install --include=dev --no-audit --no-fund
        }

        if ($LASTEXITCODE -ne 0) {
            throw "依赖安装失败（exit $LASTEXITCODE）"
        }
    }

    Write-Host '==> npm run build' -ForegroundColor Cyan
    npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "前端构建失败（exit $LASTEXITCODE）"
    }
}
finally {
    Pop-Location
}

$target = Join-Path $repoRoot 'src/ZlmSharp/WebUI/index.html'
Write-Host ''
Write-Host "完成：$target" -ForegroundColor Green
Write-Host '下一步：dotnet build / dotnet publish 会自动把它拷到 WebUI/ 下。' -ForegroundColor Green
