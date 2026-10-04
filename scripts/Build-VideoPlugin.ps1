param(
    [Parameter(Mandatory)][string]$HostSdkDir,
    [Parameter(Mandatory)][string]$Output
)
$project = Join-Path (Split-Path $PSScriptRoot) 'Plugins/TractorTool.Plugins.VideoExport/TractorTool.Plugins.VideoExport.csproj'
& (Join-Path $PSScriptRoot 'Build-Plugin.ps1') -Project $project -HostSdkDir $HostSdkDir -Output $Output
