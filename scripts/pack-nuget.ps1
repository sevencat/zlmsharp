<#
.SYNOPSIS
    批量把可复用库打成 NuGet 包，可选推送到源。

.DESCRIPTION
    版本号只有一个来源：仓库根 Directory.Build.props 里的 <Version>。
    build、pack、CLI 的 -v 输出都读它，所以升版本只改那一处。

        tools\pack-nuget.bat                    用 props 里的版本打包到 dist\nuget
        tools\pack-nuget.bat 0.2.0              本次用 0.2.0 打包（不改 props）
        tools\pack-nuget.bat 0.2.0 -set         把 0.2.0 写回 Directory.Build.props 再打包
        tools\pack-nuget.bat -push              打包后推送（需要 NUGET_API_KEY）
        tools\pack-nuget.bat 0.2.0 -set -push   改版本 + 打包 + 推送

    推送源默认 nuget.org，可用环境变量 NUGET_SOURCE 或 -Source 覆盖；
    本地文件夹源（如 .\packages）不需要 API key。

    打包项目（按依赖顺序）：
        src\ZlmSharp                Sevencat.ZlmSharp.MediaServer
        src\ZlmSharp.Extra          ZlmSharp.Extra
        src\ZlmSharp.Media.AutoGen  ZlmSharp.Media.AutoGen

.PARAMETER Version
    本次使用的版本号（SemVer）。默认取 Directory.Build.props；只有同时给
    -SetVersion 才会写回 props，否则仅在本次打包时通过 -p:Version 生效。

.PARAMETER SetVersion
    把 -Version 写回 Directory.Build.props 的 <Version>（统一版本入口）。

.PARAMETER Push
    打包完成后逐个推送到 -Source，带 --skip-duplicate（重复版本不报错）。

.PARAMETER ApiKey
    API key，默认取环境变量 NUGET_API_KEY；推送 http(s) 源时必需。

.PARAMETER Source
    推送源，默认取环境变量 NUGET_SOURCE，再退回 https://api.nuget.org/v3/index.json。

.PARAMETER OutputDirectory
    nupkg 输出目录，默认 dist\nuget（相对仓库根；已在 .gitignore 里）。

.EXAMPLE
    pwsh scripts/pack-nuget.ps1
    pwsh scripts/pack-nuget.ps1 -Version 0.2.0 -SetVersion -Push
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Version,

    [switch]$SetVersion,

    [switch]$Push,

    [string]$ApiKey = $env:NUGET_API_KEY,

    [string]$Source = $(if ([string]::IsNullOrWhiteSpace($env:NUGET_SOURCE)) { 'https://api.nuget.org/v3/index.json' } else { $env:NUGET_SOURCE }),

    [string]$OutputDirectory = 'dist\nuget'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot 'Directory.Build.props'

# 打包顺序 = 依赖顺序：Extra 依赖 ZlmSharp，Media.AutoGen 依赖两者
$packableProjects = @(
    'src\ZlmSharp\ZlmSharp.csproj'
    'src\ZlmSharp.Extra\ZlmSharp.Extra.csproj'
    'src\ZlmSharp.Media.AutoGen\ZlmSharp.Media.AutoGen.csproj'
)

$versionPattern = '<Version>\s*([^<]*?)\s*</Version>'

function Get-PackageId {
    param([string]$ProjectPath)

    $text = Get-Content -LiteralPath $ProjectPath -Raw
    $match = [regex]::Match($text, '<PackageId>\s*([^<]+?)\s*</PackageId>')
    if ($match.Success) {
        return $match.Groups[1].Value
    }

    return [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
}

if (-not (Test-Path $propsPath)) {
    throw "找不到 $propsPath"
}

foreach ($project in $packableProjects) {
    if (-not (Test-Path (Join-Path $repoRoot $project))) {
        throw "找不到项目：$project"
    }
}

# ---- 版本 ----------------------------------------------------------------

$propsText = Get-Content -LiteralPath $propsPath -Raw
$propsMatch = [regex]::Match($propsText, $versionPattern)
if (-not $propsMatch.Success) {
    throw "Directory.Build.props 里没有 <Version>，无法确定包版本"
}

$propsVersion = $propsMatch.Groups[1].Value
$effectiveVersion = if ([string]::IsNullOrWhiteSpace($Version)) { $propsVersion } else { $Version.Trim() }

$semverPattern = '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.\-]*)?(?:\+[0-9A-Za-z.\-]+)?$'
if ($effectiveVersion -notmatch $semverPattern) {
    throw "版本号不合法：'$effectiveVersion'。要 SemVer，例如 0.2.0 或 0.2.0-beta.1"
}

if ($SetVersion) {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "-SetVersion 要和版本号一起用，例如：tools\pack-nuget.bat 0.2.0 -set"
    }

    if ($effectiveVersion -eq $propsVersion) {
        Write-Host "==> Directory.Build.props 已是 $propsVersion，跳过写回" -ForegroundColor DarkGray
    }
    else {
        $bytes = [System.IO.File]::ReadAllBytes($propsPath)
        $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)

        $newText = [regex]::Replace($propsText, $versionPattern, "<Version>$effectiveVersion</Version>")
        [System.IO.File]::WriteAllText($propsPath, $newText, (New-Object System.Text.UTF8Encoding($hasBom)))

        Write-Host "==> 版本写回 Directory.Build.props：$propsVersion -> $effectiveVersion" -ForegroundColor Yellow
    }
}

# 项目里自带 <Version> 会盖掉统一版本；-p:Version 虽然能强压，但容易让人误以为 props 生效
foreach ($project in $packableProjects) {
    $projectText = Get-Content -LiteralPath (Join-Path $repoRoot $project) -Raw
    if ([regex]::IsMatch($projectText, $versionPattern)) {
        Write-Warning "$project 里还有 <Version>，它会覆盖 Directory.Build.props 的统一版本，建议删掉那一行"
    }
}

$simdyoloProject = Join-Path $repoRoot 'simdyolo\src\SimdYolo\SimdYolo.csproj'
if (Test-Path $simdyoloProject) {
    Write-Warning '检测到 simdyolo：ZlmSharp.Extra 会把 SimdYolo 作为包依赖，推送前请确认源上已有对应版本（或改用本地源 packages\）'
}

# 推送前置检查放在打包之前，避免白等一轮构建
if ($Push -and [string]::IsNullOrWhiteSpace($ApiKey) -and $Source -match '^https?://') {
    throw '-push 需要 API key：设置环境变量 NUGET_API_KEY，或用 -ApiKey 传入；本地文件夹源不需要'
}

# ---- 打包 ----------------------------------------------------------------

if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $outDir = [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    $outDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "==> 版本：$effectiveVersion" -ForegroundColor Cyan
Write-Host "==> 输出：$outDir" -ForegroundColor Cyan

Push-Location $repoRoot
try {
    foreach ($project in $packableProjects) {
        Write-Host "==> dotnet pack $project" -ForegroundColor Cyan
        & dotnet pack $project -c Release -o $outDir -p:Version=$effectiveVersion --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet pack 失败：$project（exit $LASTEXITCODE）"
        }
    }
}
finally {
    Pop-Location
}

$packages = @()
foreach ($project in $packableProjects) {
    $id = Get-PackageId (Join-Path $repoRoot $project)
    $file = Join-Path $outDir "$id.$effectiveVersion.nupkg"
    if (-not (Test-Path $file)) {
        throw "打包结束但没找到 $file"
    }

    $packages += $file
}

# ---- 推送 ----------------------------------------------------------------

if ($Push) {
    Write-Host "==> dotnet nuget push -> $Source" -ForegroundColor Cyan
    foreach ($package in $packages) {
        $pushArgs = @('nuget', 'push', $package, '-s', $Source, '--skip-duplicate')
        if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
            $pushArgs += @('-k', $ApiKey)
        }

        & dotnet @pushArgs
        if ($LASTEXITCODE -ne 0) {
            throw "推送失败：$package（exit $LASTEXITCODE）"
        }
    }
}

Write-Host ''
Write-Host "完成，版本 $effectiveVersion" -ForegroundColor Green
foreach ($package in $packages) {
    Write-Host "  $package"
}

if (-not $Push) {
    Write-Host '（未推送；加 -push 推到源，或 -Source 指定本地文件夹）' -ForegroundColor DarkGray
}
