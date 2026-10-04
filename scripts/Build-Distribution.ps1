#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$Output,
    [string]$Version,
    [string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (!$Version) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
    $Version = [string]$props.Project.PropertyGroup.Version.'#text'
}
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') {
    throw '版本必须为三段数字，可附加预发布后缀。'
}
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (Test-Path -LiteralPath $destination) { throw '分发输出目录必须是尚不存在的新目录；不会清理或覆盖旧产物。' }
for ($ancestor = Split-Path $destination; $ancestor; $ancestor = Split-Path $ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '分发输出不能经过目录链接。'
    }
}
$sourceCommit = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw '无法读取源码提交。' }
$sourceChanges = @(git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0) { throw '无法读取源码工作区状态。' }
Push-Location $repo
try {
    $sdk = dotnet --version
    if ($LASTEXITCODE -ne 0) { throw '无法读取 .NET SDK 版本。' }
} finally { Pop-Location }
$bundleName = "TractorTool-$Version-win-x64"
$bundle = Join-Path $destination "layout/$bundleName"
$assetsDir = Join-Path $destination 'assets'
New-Item -ItemType Directory -Path $assetsDir | Out-Null
& (Join-Path $PSScriptRoot 'Publish-Host.ps1') -Output $bundle -Version $Version
& (Join-Path $PSScriptRoot 'Export-DependencyNotices.ps1') `
    -AssetsFile (Join-Path $repo 'TractorTool/obj/project.assets.json') `
    -DepsFile (Join-Path $bundle 'TractorTool.deps.json') -Output (Join-Path $bundle 'Licenses')
Copy-Item -LiteralPath (Join-Path $repo 'packaging/README.txt') -Destination $bundle

# Plugins reference this published SDK. Building them must not alter the host.
$hostHashes = @{}
Get-ChildItem -LiteralPath $bundle -File -Recurse | ForEach-Object {
    $hostHashes[[IO.Path]::GetRelativePath($bundle, $_.FullName)] = (Get-FileHash -LiteralPath $_.FullName).Hash
}
$plugins = @()
foreach ($plugin in @('ClipboardHistory', 'VideoExport')) {
    $pluginOutput = Join-Path $destination "work/$plugin"
    $project = Join-Path $repo "Plugins/TractorTool.Plugins.$plugin/TractorTool.Plugins.$plugin.csproj"
    & (Join-Path $PSScriptRoot 'Build-Plugin.ps1') -Project $project -HostSdkDir $bundle -Output $pluginOutput
    $files = Join-Path $pluginOutput 'files'
    $manifest = Get-Content -LiteralPath (Join-Path $files 'plugin.json') -Raw | ConvertFrom-Json
    $installPath = "Plugins/$($manifest.id)/$($manifest.version)"
    $install = Join-Path $bundle $installPath
    New-Item -ItemType Directory -Path $install | Out-Null
    Get-ChildItem -LiteralPath $files -Force | Copy-Item -Destination $install -Recurse
    $archive = "$($manifest.id)-$($manifest.version).zip"
    Copy-Item -LiteralPath (Join-Path $pluginOutput $archive) -Destination $assetsDir
    $plugins += [ordered]@{ id = $manifest.id; version = $manifest.version; path = $installPath; archive = $archive }
}
foreach ($path in $hostHashes.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $bundle $path)).Hash -ne $hostHashes[$path]) { throw "插件构建修改了宿主：$path" }
}
$release = [ordered]@{
    schemaVersion = 1; product = 'TractorTool'; version = $Version; runtime = 'win-x64'; installationProtocol = 1
    informationalVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $bundle 'TractorTool.dll')).ProductVersion
    sourceCommit = $sourceCommit; sourceDirty = ($sourceChanges.Count -gt 0); sdkVersion = $sdk
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O'); archive = "$bundleName.zip"; bundleDirectory = $bundleName
    plugins = $plugins
}
$release | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bundle 'release.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $bundle 'release.json') -Destination $assetsDir
$files = @(Get-ChildItem -LiteralPath $bundle -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($bundle, $_.FullName).Replace('\', '/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
})
ConvertTo-Json -InputObject $files -Depth 4 | Set-Content -LiteralPath (Join-Path $bundle 'files.json') -Encoding utf8
[IO.Compression.ZipFile]::CreateFromDirectory($bundle, (Join-Path $assetsDir "$bundleName.zip"), [IO.Compression.CompressionLevel]::Optimal, $true)
Get-ChildItem -LiteralPath $assetsDir -File | Where-Object Name -ne 'SHA256SUMS' | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName).Hash, $_.Name
} | Set-Content -LiteralPath (Join-Path $assetsDir 'SHA256SUMS') -Encoding ascii
if ($CompilerPath) {
    $installerOutput = Join-Path $destination 'work/installer'
    & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -AssetsDir $assetsDir -Output $installerOutput -CompilerPath $CompilerPath
    Copy-Item -LiteralPath (Join-Path $installerOutput "assets/$bundleName-setup.exe"), (Join-Path $installerOutput 'assets/installer.json') -Destination $assetsDir
    Get-ChildItem -LiteralPath $assetsDir -File | Where-Object Name -ne 'SHA256SUMS' | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash, $_.Name
    } | Set-Content -LiteralPath (Join-Path $assetsDir 'SHA256SUMS') -Encoding ascii
}
& (Join-Path $PSScriptRoot 'Verify-Distribution.ps1') -AssetsDir $assetsDir
Write-Output "分发产物：$assetsDir"
