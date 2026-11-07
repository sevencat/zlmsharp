@echo off
setlocal

rem ---------------------------------------------------------------------------
rem 生成 NuGet 包，可选推送。逻辑在 scripts\pack-nuget.ps1。
rem
rem 版本号统一在 Directory.Build.props 的 <Version> 里改；也可以带一个版本号
rem 让脚本写回去（-set）。
rem
rem   tools\pack-nuget.bat                    用当前版本打包到 dist\nuget
rem   tools\pack-nuget.bat 0.2.0              用 0.2.0 打包（不改 props）
rem   tools\pack-nuget.bat 0.2.0 -set         把 0.2.0 写回 props 再打包
rem   tools\pack-nuget.bat -push              打包并推送（需要 NUGET_API_KEY）
rem   tools\pack-nuget.bat 0.2.0 -set -push   改版本 + 打包 + 推送
rem
rem   NUGET_API_KEY   推送用的 key
rem   NUGET_SOURCE    推送源，默认 https://api.nuget.org/v3/index.json
rem ---------------------------------------------------------------------------

set "SCRIPT=%~dp0..\scripts\pack-nuget.ps1"
if not exist "%SCRIPT%" (
    echo [error] missing %SCRIPT%
    exit /b 1
)

set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
exit /b %ERRORLEVEL%
