#requires -Version 7.4
# Read source metadata only; never build or load a plugin.
param([Parameter(Mandatory)][string]$Repository)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Repository))
$pluginRoot = Join-Path $repo 'Plugins'
if (!(Test-Path -LiteralPath $pluginRoot)) { return }
foreach ($path in @($repo, $pluginRoot)) {
    if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "随包插件路径不能经过链接：$path"
    }
}
$seenIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($directory in (Get-ChildItem -LiteralPath $pluginRoot -Directory -Force | Sort-Object Name)) {
    if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "随包插件路径不能经过链接：$($directory.FullName)" }
    $manifestPath = Join-Path $directory.FullName 'plugin.json'
    if (!(Test-Path -LiteralPath $manifestPath)) { continue }
    # Only direct plugin projects; never discover build outputs or nested helper projects.
    $projects = @(Get-ChildItem -LiteralPath $directory.FullName -Filter '*.csproj' -File -Force)
    if ($projects.Count -ne 1) { throw "插件清单同目录必须有且仅有一个 csproj：$manifestPath" }
    $project = $projects[0].FullName
    foreach ($path in @($project, $manifestPath)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "随包插件缺少文件：$path" }
        if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "随包插件路径不能经过链接：$path"
        }
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    # Match the host's stable ID and numeric plugin-version rules before creating output paths.
    $parsedVersion = $null
    if ($manifest.schemaVersion -ne 1 -or $manifest.id -isnot [string] -or
        $manifest.id -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$' -or $manifest.id.EndsWith('.') -or
        $manifest.id -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)' -or
        $manifest.version -isnot [string] -or $manifest.version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or
        ![version]::TryParse($manifest.version, [ref]$parsedVersion)) { throw "随包插件清单身份无效：$manifestPath" }
    if (!$seenIds.Add($manifest.id)) { throw "随包插件 ID 重复：$($manifest.id)" }
    [pscustomobject]@{ Project = $project; Id = $manifest.id; Version = $manifest.version }
}
