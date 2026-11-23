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
    [Dictionary[string, ReleasePackageTool]]$Tools
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
$manifest
