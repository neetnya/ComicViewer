# ComicViewer 构建与发布脚本
# 用法：
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# 脚本会自动还原 NuGet 依赖并编译发布便携式单文件版本。
# 发布产物位于 publish\ 目录下，可直接拷贝运行。

$ErrorActionPreference = 'Stop'
$projDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projDir

Write-Host '=========================================='
Write-Host '  ComicViewer 构建脚本'
Write-Host '=========================================='
Write-Host ''

Write-Host '[1/3] 检查 .NET SDK ...'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Error '未找到 dotnet 命令。请先安装 .NET 9 SDK：https://dotnet.microsoft.com/download'
    exit 1
}
$ver = (dotnet --version)
Write-Host "  dotnet 版本：$ver"
Write-Host ''

Write-Host '[2/3] 还原 NuGet 依赖（需要网络访问 nuget.org）...'
dotnet restore
if ($LASTEXITCODE -ne 0) {
    Write-Error 'NuGet 还原失败，请检查网络连接。'
    exit 1
}
Write-Host '  依赖还原成功。'
Write-Host ''

Write-Host '[3/3] 发布便携式单文件版本（Release）...'
# 使用 Publish 配置启用单文件发布（见 csproj 中 Publish 配置块）
$outDir = Join-Path $projDir 'publish'
dotnet publish -c Publish -r win-x64 --self-contained false -o $outDir
if ($LASTEXITCODE -ne 0) {
    Write-Error '发布失败。'
    exit 1
}

Write-Host ''
Write-Host '=========================================='
Write-Host '  发布成功！'
Write-Host '=========================================='
Write-Host ''
Write-Host "  产物目录：$outDir"
Write-Host '  主程序：  publish\ComicViewer.exe'
Write-Host ''
Write-Host '  使用方法：'
Write-Host '    - 直接运行 ComicViewer.exe'
Write-Host '    - 首次运行后会在同目录生成 config.json（配置文件）'
Write-Host '    - 可在设置中点击「设为默认图片查看器」关联图片'
Write-Host ''
