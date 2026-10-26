@echo off
setlocal

rem ---------------------------------------------------------------------------
rem Publish zlmsharp as a NativeAOT single-file build into dist\<runtime>.
rem
rem Usage:
rem   tools\publish.bat                 (default: win-x64)
rem   tools\publish.bat linux-x64
rem   tools\publish.bat win-arm64
rem
rem Output: dist\<runtime>\zlmsharp.exe (Linux: zlmsharp)
rem zlmsharp.ini / .env are NOT copied: both are generated on first run,
rem and log\ is created at runtime.
rem ---------------------------------------------------------------------------

set "RID=%~1"
if "%RID%"=="" set "RID=win-x64"

rem Repository root is the parent folder of this script.
pushd "%~dp0.." || exit /b 1

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [error] dotnet CLI not found in PATH. Install the .NET 10 SDK first.
    popd
    exit /b 1
)

set "OUT=dist\%RID%"
echo [publish] runtime : %RID%
echo [publish] output  : %CD%\%OUT%
echo.

dotnet publish "src\ZlmSharp.Cli\ZlmSharp.Cli.csproj" -c Release -r "%RID%" -o "%OUT%" --nologo
if errorlevel 1 (
    echo.
    echo [error] dotnet publish failed. Check the errors above, e.g.:
    echo         - NativeAOT toolchain: MSVC + Windows SDK ^(Windows^), clang + zlib1g-dev ^(Linux^)
    echo         - trim/AOT warnings from packages are errors here ^(TreatWarningsAsErrors^)
    popd
    exit /b 1
)

set "EXE=%OUT%\zlmsharp.exe"
if not exist "%EXE%" set "EXE=%OUT%\zlmsharp"
if not exist "%EXE%" (
    echo [error] publish finished but no executable found in %OUT%.
    popd
    exit /b 1
)

echo.
echo [done] %EXE%
popd
exit /b 0
