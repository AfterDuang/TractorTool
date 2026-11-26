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
class ReleaseNodeEnvironment {
    [string]$Id
    [string]$NodeVersion
    [string]$Project = 'Node/package.json'
    [string]$Lock = 'Node/pnpm-lock.yaml'
    [string]$ProjectSha256
    [string]$LockSha256
    [string]$Source = 'https://registry.npmjs.org/'
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
    [ReleaseNodeEnvironment[]]$NodeEnvironments = @()
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
            if ($manifest.ApiVersion -cnotin @('1.8', '1.9', '1.10') -or $download.Rid -cnotin @('win-x64', 'win-x86', 'win-arm64') -or
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
$verifyInputFiles = $PSCmdlet.ParameterSetName -eq 'File'
function Assert-EnvironmentInputs($Environment, [string]$Kind, [string]$ProjectName, [string]$LockName) {
    Assert-PackageRelativePath $Environment.Project
    Assert-PackageRelativePath $Environment.Lock
    $projectPath = $Environment.Project.Replace('\', '/')
    $lockPath = $Environment.Lock.Replace('\', '/')
    if ([IO.Path]::GetFileName($projectPath) -cne $ProjectName -or [IO.Path]::GetFileName($lockPath) -cne $LockName -or
        [IO.Path]::GetDirectoryName($projectPath) -cne [IO.Path]::GetDirectoryName($lockPath)) { throw "$Kind 项目和锁文件必须位于同一目录。" }
    if ($Environment.ProjectSha256 -notmatch '^[0-9a-fA-F]{64}$' -or $Environment.LockSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "$Kind 项目和锁文件必须声明 SHA256。"
    }
    if ($null -eq $Environment.Description -or $Environment.Description.Length -gt 1000) { throw "$Kind 环境用途说明无效。" }
    if ($verifyInputFiles) {
        $packageRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
        foreach ($descriptor in @(@($Environment.Project, $Environment.ProjectSha256), @($Environment.Lock, $Environment.LockSha256))) {
            $inputPath = Join-Path $packageRoot $descriptor[0]
            $item = Get-Item -LiteralPath $inputPath -Force -ErrorAction Stop
            if ($item.PSIsContainer -or $item.Length -le 0 -or $item.Length -gt 2MB) { throw "$Kind 项目或锁文件大小无效。" }
            for ($currentPath = $inputPath; $currentPath; $currentPath = [IO.Path]::GetDirectoryName($currentPath)) {
                $current = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
                if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Kind 构建输入不能经过链接。" }
            }
            if ((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -ne $descriptor[1]) { throw "$Kind 项目或锁文件的 SHA256 与声明不符。" }
        }
    }
}
foreach ($kind in @('Python', 'Node')) {
    if ($kind -eq 'Python') { $environments = $manifest.PythonEnvironments } else { $environments = $manifest.NodeEnvironments }
    if ($null -eq $environments -or @($environments).Count -gt 8) { throw "$kind 环境列表为空或过多。" }
    if (@($environments).Count -eq 0) { continue }
    if ($kind -eq 'Python' -and $manifest.ApiVersion -cnotin @('1.9', '1.10')) { throw 'Python 环境需要 API 1.9 或更高。' }
    if ($kind -eq 'Node' -and $manifest.ApiVersion -cne '1.10') { throw 'Node 环境需要 API 1.10。' }
    $environmentIds = [HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($environment in $environments) {
        if ($null -eq $environment -or $environment.Id -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$' -or
            $environment.Id.EndsWith('.') -or $environment.Id -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)' -or
            !$environmentIds.Add($environment.Id)) { throw "$kind 环境标识无效或重复。" }
        $source = $null
        if ($environment.Source.Length -gt 2048 -or ![Uri]::TryCreate($environment.Source, [UriKind]::Absolute, [ref]$source) -or
            $source.Scheme -cne 'https' -or !$source.IsDefaultPort -or $source.UserInfo -or $source.Query -or $source.Fragment) {
            if ($kind -eq 'Python') { throw 'Python 索引须为公开的 HTTPS Simple API 地址。' }
            throw 'npm registry 须为公开的 HTTPS 地址。'
        }
        if ($kind -eq 'Python') {
            if ($environment.PythonVersion -cne '3.13.18') { throw 'Python 环境须声明当前受管理版本 3.13.18。' }
            if (!$source.AbsolutePath.TrimEnd('/').EndsWith('/simple', [StringComparison]::Ordinal)) { throw 'Python 索引须为公开的 HTTPS Simple API 地址。' }
            Assert-EnvironmentInputs $environment $kind 'pyproject.toml' 'uv.lock'
        } else {
            if ($environment.NodeVersion -cne '24.22.3') { throw 'Node 环境须声明当前受管理版本 24.22.3。' }
            Assert-EnvironmentInputs $environment $kind 'package.json' 'pnpm-lock.yaml'
        }
    }
}
$manifest
