#requires -Version 7.4
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (Test-Path -LiteralPath $destination) { throw '测试输出必须是尚不存在的新目录，不覆盖已有软件或数据。' }
for ($ancestor = Split-Path $destination; $ancestor; $ancestor = Split-Path $ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '测试输出不能经过目录链接。'
    }
}
$app = Join-Path $destination 'app'
& (Join-Path $PSScriptRoot 'Publish-Host.ps1') -Output $app
& (Join-Path $PSScriptRoot 'Export-DependencyNotices.ps1') `
    -AssetsFile (Join-Path $repo 'TractorTool/obj/project.assets.json') `
    -DepsFile (Join-Path $app 'TractorTool.deps.json') -Output (Join-Path $app 'Licenses')

$package = Join-Path $destination 'plugin-package'
& (Join-Path $PSScriptRoot 'Build-Plugin.ps1') -Project (Join-Path $repo 'tests/PackageProbePlugin/PackageProbePlugin.csproj') `
    -HostSdkDir $app -Output $package
$files = Join-Path $package 'files'
$manifest = Get-Content -LiteralPath (Join-Path $files 'plugin.json') -Raw | ConvertFrom-Json
$install = Join-Path $app "Plugins/$($manifest.id)/$($manifest.version)"
New-Item -ItemType Directory -Path $install | Out-Null
Get-ChildItem -LiteralPath $files -Force | Copy-Item -Destination $install -Recurse

# Only this new test copy receives the second catalog entry; production packaging is unchanged.
$catalogFile = Join-Path $app 'PackageSources/default.json'
$catalog = Get-Content -LiteralPath $catalogFile -Raw | ConvertFrom-Json
$probeCatalog = Get-Content -LiteralPath (Join-Path $repo 'tests/PackageSources/mediainfo.json') -Raw | ConvertFrom-Json
$catalog.packages = @($catalog.packages) + @($probeCatalog.packages)
$catalog | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $catalogFile -Encoding utf8
# Always use a fresh local-only source in the sandbox, even if production later configures a remote index.
@{ schemaVersion = 1; indexUri = $null } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $app 'PackageSources/source.json') -Encoding utf8
@'
@echo off
start "" "%~dp0app\TractorTool.exe" --profile-root "%~dp0profile"
'@ | Set-Content -LiteralPath (Join-Path $destination 'Start-DependencyTest.cmd') -Encoding ascii
@'
TractorTool 外部依赖隔离测试

双击 Start-DependencyTest.cmd。不要直接打开 app/TractorTool.exe。
启动入口会把配置、日志和下载依赖放在本目录的 profile 中，不使用日常数据目录。
已预装“外部依赖测试”插件；无需服务器、手工配置或先安装 MediaInfo。

1. 打开“外部依赖测试”，点击“申请依赖并读取版本”。首次可选择先不安装，再点击重试。
2. 同意安装后，宿主从 MediaInfo 官方 HTTPS 地址下载、校验并执行版本检查。
   成功应显示 MediaInfo CLI 26.05；再次申请应直接复用，不重新下载。
3. 设置 → 外部依赖，可查看版本、插件引用及安装状态；“刷新本地目录”重新读取本副本的包资料。
4. 在插件管理中卸载测试插件，通过设置 → 关于软件退出，再双击同一启动入口重启。
   设置 → 外部依赖中 MediaInfo 应仍保留，且可以“清理未引用依赖”。
5. 如需再测，可在插件管理中安装 plugin-package 下的 Test.PackageProbe-1.0.0.zip，退出后从同一入口重启。

本目录未预下载或执行工具；下载需要网络。此测试覆盖本地索引、真实工具下载、首次确认、复用和清理。
“刷新本地目录”不是远程索引更新；正式 HTTPS 索引尚未部署，远程链路仍需单独验收。
这不是正式发布包，不包含视频或剪贴板插件。测试结束后请先从关于软件退出再处理本目录。
'@ | Set-Content -LiteralPath (Join-Path $destination 'README.txt') -Encoding utf8BOM
Write-Output "隔离测试目录已生成（尚未启动）：$destination"
