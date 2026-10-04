#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$AssetsDir,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
$assets = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AssetsDir)
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$compiler = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CompilerPath)
if (Test-Path -LiteralPath $destination) { throw '安装器输出必须是尚不存在的新目录。' }
for ($ancestor = Split-Path $destination; $ancestor; $ancestor = Split-Path $ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '安装器输出不能经过目录链接。' }
}
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '找不到 Inno Setup 编译器。' }
& (Join-Path $PSScriptRoot 'Verify-Distribution.ps1') -AssetsDir $assets
$release = Get-Content -LiteralPath (Join-Path $assets 'release.json') -Raw | ConvertFrom-Json
if ($release.installationProtocol -ne 1 -or $release.version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$' -or
    $release.bundleDirectory -ne "TractorTool-$($release.version)-win-x64" -or $release.archive -ne "$($release.bundleDirectory).zip") {
    throw '安装器要求含维护互斥保护的 Windows x64 分发布局（installationProtocol=1）。'
}
$sourceZip = Join-Path $assets $release.archive
$payload = Join-Path $destination 'payload'
New-Item -ItemType Directory -Path $payload | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($sourceZip, $payload)
$bundle = Join-Path $payload $release.bundleDirectory
$outputAssets = Join-Path $destination 'assets'
New-Item -ItemType Directory -Path $outputAssets | Out-Null
# Enumerate only the verified ZIP snapshot. Never glob the live application directory.
$entries = foreach ($file in (Get-ChildItem -LiteralPath $bundle -File -Recurse | Sort-Object FullName)) {
    $relative = [IO.Path]::GetRelativePath($bundle, $file.FullName)
    if ($relative -match '[{};\r\n]') { throw "安装器文件路径包含不支持的脚本字符：$relative" }
    $parent = Split-Path $relative
    $target = if ($parent) { "{app}\$parent" } else { '{app}' }
    $flags = if ($relative.StartsWith('Plugins\', [StringComparison]::OrdinalIgnoreCase)) {
        'ignoreversion uninsneveruninstall; Check: SeedPlugins; BeforeInstall: RecordSeed'
    } else { 'ignoreversion' }
    'Source: "{0}"; DestDir: "{1}"; Flags: {2}' -f $file.FullName, $target, $flags
}
$include = Join-Path $destination 'Payload.iss'
$entries | Set-Content -LiteralPath $include -Encoding utf8BOM
$repo = Split-Path $PSScriptRoot
$template = Join-Path $repo 'packaging/TractorTool.iss'
& $compiler "/DAppVersion=$($release.version)" "/DAppNumericVersion=$(($release.version -split '-')[0])" "/DSeedHash=$((Get-FileHash -LiteralPath $sourceZip).Hash)" "/DPayloadInclude=$include" "/O$outputAssets" $template
if ($LASTEXITCODE -ne 0) { throw '安装器编译失败。' }
$name = "TractorTool-$($release.version)-win-x64-setup.exe"
if (!(Test-Path -LiteralPath (Join-Path $outputAssets $name))) { throw '未生成预期安装器。' }
[ordered]@{
    schemaVersion = 1; version = $release.version; archive = $name; compiler = 'Inno Setup 6.7.3'
    sourceArchive = $release.archive; sourceSha256 = (Get-FileHash -LiteralPath $sourceZip).Hash
    sourceCommit = $release.sourceCommit; sourceDirty = $release.sourceDirty
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputAssets 'installer.json') -Encoding utf8
Get-ChildItem -LiteralPath $outputAssets -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash, $_.Name
} | Set-Content -LiteralPath (Join-Path $outputAssets 'SHA256SUMS') -Encoding ascii
Write-Output "安装器产物：$outputAssets"
