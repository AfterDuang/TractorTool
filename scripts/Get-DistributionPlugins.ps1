#requires -Version 7.4
# Read source metadata only; never build or load a plugin.
param([Parameter(Mandatory)][string]$Repository)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Repository))
$pluginRoot = (Join-Path $repo 'Plugins') + [IO.Path]::DirectorySeparatorChar
$projects = Get-Content -LiteralPath (Join-Path $repo 'packaging/plugins.json') -Raw | ConvertFrom-Json -NoEnumerate
if ($projects -isnot [array]) { throw '随包插件清单必须是工程相对路径数组。' }
$seenProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$seenIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in $projects) {
    if ($relative -isnot [string] -or [string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
        [IO.Path]::GetExtension($relative) -ine '.csproj') { throw "无效的插件工程相对路径：$relative" }
    $project = [IO.Path]::GetFullPath((Join-Path $repo $relative))
    if (!$project.StartsWith($pluginRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "随包插件工程必须位于仓库 Plugins 内：$relative" }
    if (!$seenProjects.Add($project)) { throw "随包插件工程重复：$relative" }
    $manifestPath = Join-Path (Split-Path $project) 'plugin.json'
    foreach ($path in @($project, $manifestPath)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "随包插件缺少文件：$path" }
        for ($current = $path; ; $current = Split-Path $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "随包插件路径不能经过链接：$current"
            }
            if ($current -eq $repo) { break }
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
