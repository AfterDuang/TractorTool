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
class ReleasePluginManifest {
    [int]$SchemaVersion
    [string]$Id
    [string]$Version
    [string]$ApiVersion
    [string]$AvaloniaVersion
    [string]$UiApiVersion
    [ReleasePackageDependency[]]$Dependencies
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
            if ($manifest.ApiVersion -cne '1.8' -or $download.Rid -cnotin @('win-x64', 'win-x86', 'win-arm64') -or
                $download.Tools.get_Count() -ne 0 -or $download.Libraries.get_Count() -notin 1..32) { throw '原生运行库需要 API 1.8、Windows 平台和 DLL 入口。' }
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
$manifest
