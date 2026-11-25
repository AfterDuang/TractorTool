#requires -Version 7.4
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $CacheDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'MediaCompare 的预制运行库当前仅支持 Windows x64 构建。' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$materials = Join-Path $PSScriptRoot '../Native/Runtime'
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $repoRoot 'artifacts/media-compare-runtime' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $CacheDirectory 'runtime' }
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

function Assert-WorkspacePath([string] $Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "生成路径必须位于工作区内：$full"
    }
    for ($current = $full; $current -and $current -ne $repoRoot; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "生成路径不能穿过重解析点：$current"
        }
    }
}
Assert-WorkspacePath $CacheDirectory
Assert-WorkspacePath $OutputDirectory
if ($CacheDirectory -eq $OutputDirectory) { throw '输出目录不能与下载缓存目录相同。' }
[void][IO.Directory]::CreateDirectory($CacheDirectory)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputDirectory))
$configPath = Join-Path $materials 'runtime-sources.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.schemaVersion -ne 1) { throw '不支持的运行库清单格式。' }
$licenseManifestPath = Join-Path $materials 'license-manifest.json'
$licenseManifest = Get-Content -LiteralPath $licenseManifestPath -Raw | ConvertFrom-Json
$licenseMaterials = Join-Path $materials 'licenses'
if ($licenseManifest.schemaVersion -ne 1 -or @(Get-ChildItem -LiteralPath $licenseMaterials -Recurse -File).Count -ne $licenseManifest.files.Count) {
    throw '原文许可证材料与清单不一致。'
}
foreach ($entry in $licenseManifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $licenseMaterials $entry.path))
    if (-not $path.StartsWith([IO.Path]::GetFullPath($licenseMaterials) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "原文许可证校验失败：$($entry.path)" }
}
$inputs = @($PSCommandPath, $configPath, (Join-Path $materials 'PeImports.cs.txt'), $licenseManifestPath, (Join-Path $materials 'RUNTIME-NOTICE.md'))
$fingerprintText = ($inputs | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) -join ':'
$fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($fingerprintText)))

function Get-Archive($Source) {
    $downloadRoot = Join-Path $CacheDirectory 'downloads'
    Assert-WorkspacePath $downloadRoot
    [void][IO.Directory]::CreateDirectory($downloadRoot)
    $path = Join-Path $downloadRoot $Source.fileName
    Assert-WorkspacePath $path
    if (-not (Test-Path -LiteralPath $path)) {
        $partial = "$path.$([Guid]::NewGuid().ToString('N')).partial"
        Assert-WorkspacePath $partial
        Write-Host "下载 $($Source.fileName)"
        Invoke-WebRequest -Uri $Source.url -OutFile $partial
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Source.sha256) {
            throw "下载 SHA256 不匹配，保留待检查文件：$partial"
        }
        [IO.File]::Move($partial, $path)
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Source.sha256) {
        throw "缓存 SHA256 不匹配，请检查后移除该缓存：$path"
    }
    return $path
}

function Test-PreparedRuntime {
    $manifestPath = Join-Path $OutputDirectory 'runtime-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $false }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.schemaVersion -ne 1 -or $manifest.fingerprint -ne $fingerprint) { return $false }
        $actual = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File)
        if ($actual.Count -ne $manifest.files.Count + 1) { return $false }
        foreach ($item in $manifest.files) {
            $path = [IO.Path]::GetFullPath((Join-Path $OutputDirectory $item.path))
            if (-not $path.StartsWith($OutputDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return $false }
            Assert-WorkspacePath $path
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
                (Get-Item -LiteralPath $path).Length -ne $item.bytes -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item.sha256) { return $false }
        }
        $totalBytes = ($actual | Measure-Object -Property Length -Sum).Sum
        Write-Host "GStreamer $($config.gstreamerVersion) 运行库已校验：$totalBytes bytes，$($actual.Count) files"
        return $true
    } catch { return $false }
}

# Serialize concurrent project builds sharing this output/cache. Never accept a partially prepared tree.
$lockPath = Join-Path $CacheDirectory 'prepare.lock'
Assert-WorkspacePath $lockPath
$lock = $null
$deadline = [DateTime]::UtcNow.AddMinutes(10)
while (-not $lock) {
    try { $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch [IO.IOException] {
        if ([DateTime]::UtcNow -ge $deadline) { throw '等待 GStreamer 准备任务超时。' }
        Start-Sleep -Milliseconds 250
    }
}
try {
    if (Test-PreparedRuntime) { return }
    if (Test-Path -LiteralPath $OutputDirectory) {
        $existingManifest = Join-Path $OutputDirectory 'runtime-manifest.json'
        if (@(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
            $existing = if (Test-Path -LiteralPath $existingManifest) { Get-Content -LiteralPath $existingManifest -Raw | ConvertFrom-Json } else { $null }
            if (-not $existing -or $existing.schemaVersion -ne 1 -or $existing.rid -ne $config.rid -or $existing.fingerprint -notmatch '^[a-fA-F0-9]{64}$') {
                throw "输出位置不是此脚本管理的运行库，拒绝覆盖：$OutputDirectory"
            }
        }
    }
    $archives = @($config.sources | ForEach-Object { Get-Archive $_ })
    $stage = Join-Path $CacheDirectory ('.prepare-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    try {
        $upstream = Join-Path $stage 'upstream'
        # Official wheels are ZIP containers. Read native payload only; no Python or installer execution.
        foreach ($archive in $archives) {
            $zip = [IO.Compression.ZipFile]::OpenRead($archive)
            try {
                foreach ($entry in $zip.Entries) {
                    if ($entry.FullName -notmatch '^[^/]+\.data/purelib/[^/]+/(?<relative>(?:bin|lib/gstreamer-1\.0)/[^/]+\.dll)$') { continue }
                    $destination = [IO.Path]::GetFullPath((Join-Path $upstream $Matches.relative))
                    if (-not $destination.StartsWith($upstream + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '非法原生包路径。' }
                    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
                    # Official component wheels overlap (for example dav1d); accept only identical bytes.
                    if (Test-Path -LiteralPath $destination) {
                        $stream = $entry.Open()
                        try { $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
                        finally { $stream.Dispose() }
                        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entryHash) {
                            throw "原生包包含冲突文件：$($Matches.relative)"
                        }
                    } else { [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination) }
                }
            } finally { $zip.Dispose() }
        }
        if (-not ('MediaComparePeImports' -as [type])) { Add-Type -TypeDefinition (Get-Content -LiteralPath (Join-Path $materials 'PeImports.cs.txt') -Raw) }
        $selected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        $systemImports = [Collections.Generic.SortedSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $pending = [Collections.Generic.Queue[string]]::new()
        foreach ($module in $config.modules) { $pending.Enqueue((Join-Path $upstream "lib/gstreamer-1.0/$module.dll")) }
        while ($pending.Count -gt 0) {
            $path = $pending.Dequeue()
            if ($selected.ContainsKey($path)) { continue }
            $imports = [MediaComparePeImports]::Read($path)
            $selected.Add($path, $imports)
            foreach ($name in $imports) {
                $candidate = Join-Path $upstream "bin/$name"
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $pending.Enqueue($candidate) }
                elseif ($name -match '^(api-ms-win-|ext-ms-win-).+\.dll$' -or $name -in $config.windowsImports) { [void]$systemImports.Add($name) }
                else { throw "未解析的 PE 导入依赖：$([IO.Path]::GetFileName($path)) -> $name" }
            }
        }
        $runtime = Join-Path $stage 'runtime'
        foreach ($path in $selected.Keys) {
            $destination = Join-Path $runtime ([IO.Path]::GetRelativePath($upstream, $path))
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            [IO.File]::Copy($path, $destination)
        }
        $licenseRoot = Join-Path $runtime 'licenses'
        [void][IO.Directory]::CreateDirectory($licenseRoot)
        foreach ($component in $config.licenses) {
            $source = Join-Path $licenseMaterials $component
            if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "缺少上游许可证：$component" }
            Copy-Item -LiteralPath $source -Destination $licenseRoot -Recurse
        }
        $msvcLicense = Join-Path $materials $config.msvcLicense.path
        if ((Get-FileHash -LiteralPath $msvcLicense -Algorithm SHA256).Hash -ne $config.msvcLicense.sha256) { throw 'MSVC 原文许可证校验失败。' }
        Copy-Item -LiteralPath (Split-Path -Parent $msvcLicense) -Destination $licenseRoot -Recurse
        Copy-Item -LiteralPath (Join-Path $materials 'RUNTIME-NOTICE.md') -Destination (Join-Path $runtime 'NOTICE.md')
        Copy-Item -LiteralPath $configPath -Destination (Join-Path $runtime 'runtime-sources.json')
        Copy-Item -LiteralPath $licenseManifestPath -Destination (Join-Path $runtime 'license-manifest.json')
        $files = @(Get-ChildItem -LiteralPath $runtime -Recurse -File | Sort-Object FullName | ForEach-Object {
            @{ path = [IO.Path]::GetRelativePath($runtime, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
        $total = ($files | Measure-Object -Property bytes -Sum).Sum
        if ($total -ge 240MB) { throw "运行库超过预留预算 240 MiB：$total bytes；插件总体上限 256 MiB。" }
        $imports = @($selected.Keys | Sort-Object | ForEach-Object { @{ path = [IO.Path]::GetRelativePath($upstream, $_).Replace('\', '/'); imports = $selected[$_] } })
        $manifest = @{ schemaVersion = 1; fingerprint = $fingerprint; version = $config.gstreamerVersion; rid = $config.rid; payloadBytes = $total; modules = $config.modules; files = $files; nativeImports = $imports; windowsImports = @($systemImports) }
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runtime 'runtime-manifest.json') -Encoding utf8NoBOM
        if (Test-Path -LiteralPath $OutputDirectory) {
            Assert-WorkspacePath $OutputDirectory
            $backup = "$OutputDirectory.replaced-$([Guid]::NewGuid().ToString('N'))"
            Assert-WorkspacePath $backup
            [IO.Directory]::Move($OutputDirectory, $backup)
            Write-Host "旧生成目录保留在：$backup"
        }
        [IO.Directory]::Move($runtime, $OutputDirectory)
        $actualFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File)
        $actualBytes = ($actualFiles | Measure-Object -Property Length -Sum).Sum
        Write-Host "已准备 GStreamer $($config.gstreamerVersion)：$actualBytes bytes，$($actualFiles.Count) files，输出 $OutputDirectory"
    } finally {
        # Delete only the random extraction directory created by this invocation, never an output or cache root.
        Assert-WorkspacePath $stage
        if ([IO.Path]::GetDirectoryName($stage) -ne $CacheDirectory -or -not [IO.Path]::GetFileName($stage).StartsWith('.prepare-')) { throw '拒绝清理非本次提取目录。' }
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
} finally { $lock.Dispose() }
