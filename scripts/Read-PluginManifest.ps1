#requires -Version 7.4
using namespace System.Collections.Generic
using namespace System.Text.Json
param(
    [Parameter(Mandatory, ParameterSetName = 'File')][string]$Path,
    [Parameter(Mandatory, ParameterSetName = 'Text')][string]$Json
)
# Only project fields consumed by build and verification. JsonSerializerDefaults.Web matches Core:
# schema properties ignore case, while plugin-defined tool IDs are ordinal dictionary keys.
class ReleasePackageTool {
    [string]$Path
}
class ReleasePackageDownload {
    [string]$Rid
    [string]$Sha256
    [string]$Kind = 'tool'
    [Dictionary[string, ReleasePackageTool]]$Tools = [Dictionary[string, ReleasePackageTool]]::new([StringComparer]::Ordinal)
    [Dictionary[string, string]]$Libraries = [Dictionary[string, string]]::new([StringComparer]::Ordinal)
}
class ReleasePackageDependency {
    [string]$Id
    [string]$Version
    [ReleasePackageDownload[]]$Downloads
}
class ReleasePythonEnvironment {
    [string]$Id
    [string]$PythonVersion
    [string]$Project = 'Python/pyproject.toml'
    [string]$Lock = 'Python/uv.lock'
    [string]$ProjectSha256
    [string]$LockSha256
    [string]$Source = 'https://pypi.org/simple/'
    [string]$Description = ''
}
class ReleasePluginManifest {
    [int]$SchemaVersion
    [string]$Id
    [string]$Version
    [string]$ApiVersion
    [string]$AvaloniaVersion
    [string]$UiApiVersion
    [ReleasePackageDependency[]]$Dependencies
    [ReleasePythonEnvironment[]]$PythonEnvironments = @()
}
if ($PSCmdlet.ParameterSetName -eq 'File') { $Json = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop }
try {
    $manifest = [JsonSerializer]::Deserialize($Json, [ReleasePluginManifest], [JsonSerializerOptions]::new([JsonSerializerDefaults]::Web))
} catch [JsonException] {
    throw [IO.InvalidDataException]::new("插件清单 JSON 无效。$($_.Exception.Message)", $_.Exception)
}
if ($null -eq $manifest) { throw '插件清单 JSON 为空。' }
function Assert-PackageRelativePath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 240) { throw '依赖入口路径为空或过长。' }
    $parts = $Value.Replace('\', '/').Split('/')
    if ($parts.Count -gt 32) { throw '依赖入口路径层级过多。' }
    foreach ($part in $parts) {
        if (!$part -or $part -in @('.', '..') -or $part -match '[. ]$|[<>:"|?*\x00-\x1F]|^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') {
            throw "依赖入口必须是安全的包内相对路径：$Value"
        }
    }
}
foreach ($dependency in $manifest.Dependencies) {
    $kinds = [HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($download in $dependency.Downloads) {
        if ($null -eq $download -or $null -eq $download.Tools -or $null -eq $download.Libraries) {
            throw '依赖下载资料或入口列表为空。'
        }
        if ([string]::Equals($download.Kind, 'tool', [StringComparison]::OrdinalIgnoreCase)) {
            if ($download.Tools.get_Count() -notin 1..32 -or $download.Libraries.get_Count() -ne 0) { throw '工具包入口声明无效。' }
        } elseif ([string]::Equals($download.Kind, 'nativeLibrary', [StringComparison]::OrdinalIgnoreCase)) {
            if ($manifest.ApiVersion -cnotin @('1.8', '1.9') -or $download.Rid -cnotin @('win-x64', 'win-x86', 'win-arm64') -or
                $download.Tools.get_Count() -ne 0 -or $download.Libraries.get_Count() -notin 1..32) { throw '原生运行库需要 API 1.8 或更高、Windows 平台和 DLL 入口。' }
            foreach ($library in $download.Libraries.GetEnumerator()) {
                $path = $library.Value
                if (!$path -or !$path.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) { throw '原生运行库入口必须是 DLL。' }
            }
        } else { throw "未知的依赖类型：$($download.Kind)" }
        foreach ($tool in $download.Tools.GetEnumerator()) { Assert-PackageRelativePath $tool.Value.Path }
        foreach ($library in $download.Libraries.GetEnumerator()) { Assert-PackageRelativePath $library.Value }
        $null = $kinds.Add($download.Kind)
    }
    if ($kinds.Count -gt 1) { throw '同一依赖在不同平台必须声明相同的包类型。' }
}
if ($null -eq $manifest.PythonEnvironments -or $manifest.PythonEnvironments.Count -gt 8) { throw 'Python 环境列表为空或过多。' }
if ($manifest.PythonEnvironments.Count -ne 0 -and $manifest.ApiVersion -cne '1.9') { throw 'Python 环境需要 API 1.9。' }
$environmentIds = [HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($environment in $manifest.PythonEnvironments) {
    if ($null -eq $environment -or $environment.Id -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$' -or
        $environment.Id.EndsWith('.') -or $environment.Id -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)' -or
        !$environmentIds.Add($environment.Id)) { throw 'Python 环境标识无效或重复。' }
    if ($environment.PythonVersion -cne '3.13.18') { throw 'Python 环境须声明当前受管理版本 3.13.18。' }
    Assert-PackageRelativePath $environment.Project
    Assert-PackageRelativePath $environment.Lock
    $projectPath = $environment.Project.Replace('\', '/')
    $lockPath = $environment.Lock.Replace('\', '/')
    if ([IO.Path]::GetFileName($projectPath) -cne 'pyproject.toml' -or [IO.Path]::GetFileName($lockPath) -cne 'uv.lock' -or
        [IO.Path]::GetDirectoryName($projectPath) -cne [IO.Path]::GetDirectoryName($lockPath)) { throw 'Python 项目和锁文件必须位于同一目录。' }
    if ($environment.ProjectSha256 -notmatch '^[0-9a-fA-F]{64}$' -or $environment.LockSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Python 项目和锁文件必须声明 SHA256。' }
    $index = $null
    if ($environment.Source.Length -gt 2048 -or ![Uri]::TryCreate($environment.Source, [UriKind]::Absolute, [ref]$index) -or
        $index.Scheme -cne 'https' -or !$index.IsDefaultPort -or $index.UserInfo -or $index.Query -or $index.Fragment -or
        !$index.AbsolutePath.TrimEnd('/').EndsWith('/simple', [StringComparison]::Ordinal)) { throw 'Python 索引须为公开的 HTTPS Simple API 地址。' }
    if ($null -eq $environment.Description -or $environment.Description.Length -gt 1000) { throw 'Python 环境用途说明无效。' }
    if ($PSCmdlet.ParameterSetName -eq 'File') {
        $packageRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
        foreach ($descriptor in @(@($environment.Project, $environment.ProjectSha256), @($environment.Lock, $environment.LockSha256))) {
            $inputPath = Join-Path $packageRoot $descriptor[0]
            $item = Get-Item -LiteralPath $inputPath -Force -ErrorAction Stop
            if ($item.PSIsContainer -or $item.Length -le 0 -or $item.Length -gt 2MB) { throw 'Python 项目或锁文件大小无效。' }
            for ($currentPath = $inputPath; $currentPath; $currentPath = [IO.Path]::GetDirectoryName($currentPath)) {
                $current = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
                if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Python 构建输入不能经过链接。' }
            }
            if ((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -ne $descriptor[1]) { throw 'Python 项目或锁文件的 SHA256 与声明不符。' }
        }
    }
}
$manifest
