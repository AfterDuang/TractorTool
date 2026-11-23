#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$HostSdkDir,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$hostSdk = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($HostSdkDir)
foreach ($assembly in @('TractorTool.Contracts.dll', 'TractorTool.UI.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $hostSdk $assembly) -PathType Leaf)) { throw 'HostSdkDir 必须指向已经发布的宿主。' }
}
$hostDeps = Get-Content -LiteralPath (Join-Path $hostSdk 'TractorTool.deps.json') -Raw | ConvertFrom-Json -AsHashtable
. (Join-Path $PSScriptRoot 'PluginAssemblyPolicy.ps1')
$hostConfig = Get-Content -LiteralPath (Join-Path $hostSdk 'TractorTool.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
$sdkPolicy = Get-PluginSdkAssemblyPolicy $hostConfig $hostDeps
$hostAvalonia = @($hostDeps.libraries.Keys | Where-Object { $_ -like 'Avalonia/*' -and $hostDeps.libraries[$_].type -eq 'package' })
if ($hostAvalonia.Count -ne 1) { throw '已发布宿主的 TractorTool.deps.json 缺少唯一的 Avalonia 包版本。' }
$hostAvaloniaVersion = $hostAvalonia[0].Split('/')[1]
function Read-PluginManifest([string]$Path, [string]$AvaloniaVersion) {
    $manifest = & (Join-Path $PSScriptRoot 'Read-PluginManifest.ps1') -Path $Path
    if ($manifest.avaloniaVersion -isnot [string] -or $manifest.avaloniaVersion -cne $AvaloniaVersion) {
        throw "Avalonia 版本不兼容：$Path 声明 '$($manifest.avaloniaVersion)'，目标宿主为 '$AvaloniaVersion'。"
    }
    $manifest
}
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if ((Test-Path -LiteralPath $destination) -and (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)) {
    throw '插件输出目录必须为空，避免覆盖旧包。'
}
$Project = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Project)
$projectRoot = Split-Path $Project
$manifest = Read-PluginManifest (Join-Path $projectRoot 'plugin.json') $hostAvaloniaVersion
$files = Join-Path $destination 'files'
# Normal restore is required when switching between source SDK references and published DLLs.
$assetsPathFile = [IO.Path]::GetTempFileName()
Push-Location $projectRoot
try {
    dotnet build $Project -t:Rebuild -c Release -m:1 -nr:false "-p:HostSdkDir=$hostSdk" "-p:Version=$($manifest.version)" -o $files `
        -getProperty:ProjectAssetsFile "-getResultOutputFile:$assetsPathFile"
    if ($LASTEXITCODE -ne 0) { throw '插件构建失败。' }
    $assetsPath = (Get-Content -LiteralPath $assetsPathFile -Raw).Trim()
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
} finally {
    Pop-Location
    Remove-Item -LiteralPath $assetsPathFile
}
$builtManifest = Read-PluginManifest (Join-Path $files 'plugin.json') $hostAvaloniaVersion
if ($builtManifest.id -cne $manifest.id -or $builtManifest.version -cne $manifest.version) { throw '构建输出与源清单的插件 ID 或版本不一致。' }
foreach ($package in $assets.libraries.Keys) {
    if ($package -like 'Avalonia/*' -and $assets.libraries[$package].type -eq 'package' -and $package.Split('/')[1] -cne $hostAvaloniaVersion) {
        throw "插件还原了 $package，目标宿主使用 $($hostAvalonia[0])。请按目标宿主基线重新编译。"
    }
}
foreach ($file in Get-ChildItem -LiteralPath $files -Recurse -File) {
    Assert-PluginSdkFile $sdkPolicy $file.Name $builtManifest.uiApiVersion
}
$licenses = Join-Path $files 'Licenses'
# Only collect this known license when the package actually ships; MVVM is not an SDK requirement.
if (Test-Path -LiteralPath (Join-Path $files 'CommunityToolkit.Mvvm.dll') -PathType Leaf) {
    $toolkit = @($assets.libraries.Keys | Where-Object { $_ -like 'CommunityToolkit.Mvvm/*' })
    if ($toolkit.Count -ne 1) { throw '未找到唯一的 MVVM 还原版本。' }
    $packagePath = $assets.libraries[$toolkit[0]].path
    $packageRoot = @($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $packagePath } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ 'License.md') }) | Select-Object -First 1
    if (!$packageRoot) { throw '未找到本次还原包的 MVVM 许可。' }
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $packageRoot 'License.md') -Destination (Join-Path $licenses 'CommunityToolkit.Mvvm.md')
    Copy-Item -LiteralPath (Join-Path $packageRoot 'ThirdPartyNotices.txt') -Destination $licenses
}
$privateLicenses = Join-Path $projectRoot 'Licenses'
if (Test-Path -LiteralPath $privateLicenses) {
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Get-ChildItem -LiteralPath $privateLicenses | Copy-Item -Destination $licenses -Recurse
}
$archive = Join-Path $destination "$($manifest.id)-$($manifest.version).zip"
[IO.Compression.ZipFile]::CreateFromDirectory($files, $archive)
(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
Write-Output "插件 ZIP：$archive"
