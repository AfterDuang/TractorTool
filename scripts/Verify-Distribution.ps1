#requires -Version 7.4
# Static artifact checks only: never load assemblies or start the application.
param(
    [Parameter(Mandatory)][string]$AssetsDir,
    [string]$ExpectedTag,
    [string]$ExpectedCommit
)
$ErrorActionPreference = 'Stop'
$root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AssetsDir)
$checksums = @{}
foreach ($line in (Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS'))) {
    if ($line -notmatch '^([A-Fa-f0-9]{64})  ([^/\\:]+)$' -or $Matches[2] -in @('.', '..', 'SHA256SUMS')) { throw 'SHA256SUMS 格式无效。' }
    $name = $Matches[2]
    if ($checksums.ContainsKey($name)) { throw "重复的校验文件名：$name" }
    $checksums[$name] = $Matches[1]
    if ((Get-FileHash -LiteralPath (Join-Path $root $name)).Hash -ne $checksums[$name]) { throw "产物哈希不匹配：$name" }
}
$actual = @(Get-ChildItem -LiteralPath $root -File | Where-Object Name -ne 'SHA256SUMS')
if ($actual.Count -ne $checksums.Count) { throw '产物存在未登记文件。' }
$release = Get-Content -LiteralPath (Join-Path $root 'release.json') -Raw | ConvertFrom-Json
if ($ExpectedTag -or $ExpectedCommit) {
    if ($ExpectedTag -cnotmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$' -or
        $ExpectedTag -cne "v$($release.version)" -or $ExpectedCommit -notmatch '^[a-fA-F0-9]{40}$' -or
        $release.sourceCommit -ne $ExpectedCommit -or $release.sourceDirty -isnot [bool] -or $release.sourceDirty -or
        $release.schemaVersion -ne 1 -or $release.product -cne 'TractorTool' -or $release.runtime -cne 'win-x64') {
        throw 'Release 身份不匹配：标签、构建版本、源码提交必须一致，且构建工作区必须干净。'
    }
    if (!$checksums.ContainsKey('installer.json')) { throw 'Release 缺少 Windows 安装器。' }
}
if (!$checksums.ContainsKey($release.archive)) { throw '宿主 ZIP 未登记。' }
if ($checksums.ContainsKey('installer.json') -ne $checksums.ContainsKey("TractorTool-$($release.version)-win-x64-setup.exe")) { throw '安装器与来源记录必须成对提供。' }
if ($checksums.ContainsKey('installer.json')) {
    $installer = Get-Content -LiteralPath (Join-Path $root 'installer.json') -Raw | ConvertFrom-Json
    if ($installer.version -ne $release.version -or $installer.archive -ne "TractorTool-$($release.version)-win-x64-setup.exe" -or
        !$checksums.ContainsKey($installer.archive) -or $installer.sourceArchive -ne $release.archive -or
        $installer.sourceSha256 -ne $checksums[$release.archive] -or $installer.sourceCommit -ne $release.sourceCommit -or
        $installer.sourceDirty -ne $release.sourceDirty) { throw '安装器来源与 ZIP 分发不一致。' }
}
function Read-ZipText($Entry) {
    if (!$Entry) { throw 'ZIP 缺少必要文件。' }
    $reader = [IO.StreamReader]::new($Entry.Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Get-ZipHash($Entry) {
    $stream = $Entry.Open()
    try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
}
function Get-ZipFiles($Archive, [string]$Prefix) {
    $result = @{}
    $pathKinds = @{}
    foreach ($entry in $Archive.Entries) {
        $name = $entry.FullName
        if (!$name.StartsWith($Prefix, [StringComparison]::Ordinal) -or $name.Contains('\') -or $name.StartsWith('/')) { throw "无效 ZIP 路径：$name" }
        # Match Windows extraction and the plugin installer's segment rules.
        $parts = $name.TrimEnd('/').Split('/')
        $path = ''
        for ($i = 0; $i -lt $parts.Length; $i++) {
            $part = $parts[$i]
            if (!$part -or $part -match '[. ]$|[<>:"|?*\x00-\x1F]|^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw "无效 ZIP 路径：$name" }
            $path = if ($path) { "$path/$part" } else { $part }
            $kind = if ($i -lt $parts.Length - 1 -or $name.EndsWith('/')) { 'directory' } else { 'file' }
            if ($pathKinds.ContainsKey($path) -and ($pathKinds[$path] -ne $kind -or $kind -eq 'file')) { throw "ZIP 路径冲突：$name" }
            $pathKinds[$path] = $kind
        }
        if ($name.EndsWith('/')) { continue }
        $relative = $name.Substring($Prefix.Length)
        $result[$relative] = $entry
    }
    return $result
}
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $root $release.archive))
try {
    $entries = Get-ZipFiles $zip "$($release.bundleDirectory)/"
    $inventory = @(Read-ZipText $entries['files.json'] | ConvertFrom-Json)
    $listed = @{}
    foreach ($item in $inventory) {
        if ($listed.ContainsKey($item.path) -or !$entries.ContainsKey($item.path)) { throw "文件清单无效：$($item.path)" }
        $entry = $entries[$item.path]
        if ($entry.Length -ne $item.size -or (Get-ZipHash $entry) -ne $item.sha256) { throw "ZIP 内容校验失败：$($item.path)" }
        $listed[$item.path] = $item.sha256
    }
    if ($entries.Count -ne $listed.Count + 1 -or $listed.ContainsKey('files.json')) { throw 'ZIP 存在清单外文件。' }
    foreach ($required in @('TractorTool.exe', 'TractorTool.dll', 'TractorTool.Contracts.dll', 'TractorTool.UI.dll', 'THIRD-PARTY-NOTICES.md', 'Licenses/dependencies.json')) {
        if (!$listed.ContainsKey($required)) { throw "缺少分发文件：$required" }
    }
    if ((Get-ZipHash $entries['release.json']) -ne $checksums['release.json']) { throw '内外发布元数据不一致。' }
    if (@($entries.Keys | Where-Object { $_ -match '(^|/)(ffmpeg|ffprobe)\.exe$|(^|/)bootstrap\.json$|(^|/)(Config|Data|Logs|Cache|\.management)/|(^|/)(Test\.Independent|IndependentPlugin)(/|\.)' }).Count) { throw '分发包混入外部工具、用户数据或测试插件。' }
    foreach ($plugin in $release.plugins) {
        if (!$checksums.ContainsKey($plugin.archive)) { throw '插件 ZIP 未登记。' }
        $pluginZip = [IO.Compression.ZipFile]::OpenRead((Join-Path $root $plugin.archive))
        try {
            $pluginFiles = Get-ZipFiles $pluginZip ''
            $manifest = Read-ZipText $pluginFiles['plugin.json'] | ConvertFrom-Json
            if ($manifest.id -ne $plugin.id -or $manifest.version -ne $plugin.version) { throw '插件版本不一致。' }
            $prefix = "$($plugin.path)/"
            if (@($listed.Keys | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) }).Count -ne $pluginFiles.Count) { throw '整包与独立插件文件数量不一致。' }
            foreach ($path in $pluginFiles.Keys) {
                if ((Get-ZipHash $pluginFiles[$path]) -ne $listed["$prefix$path"]) { throw "整包与独立插件不一致：$path" }
                if ([IO.Path]::GetFileNameWithoutExtension($path) -match '^(TractorTool(\.Core|\.Contracts|\.UI)?|Avalonia(\..*)?|FluentAvalonia|MicroCom\.Runtime)$' -and $path.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) { throw "插件夹带共享程序集：$path" }
            }
        } finally { $pluginZip.Dispose() }
    }
    Write-Output "静态分发校验通过：$($listed.Count) 个文件，$($release.plugins.Count) 个插件；未运行应用。"
} finally { $zip.Dispose() }
