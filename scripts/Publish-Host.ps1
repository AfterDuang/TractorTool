param(
    [Parameter(Mandatory)][string]$Output,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [ValidateNotNullOrEmpty()][string]$Version
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$destination = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $destination) {
    if (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1) { throw '发布目标必须为空目录，避免覆盖现有安装。' }
}
$publishArgs = @('publish', (Join-Path $repo 'TractorTool/TractorTool.csproj'), '-c', $Configuration,
    '-r', $Runtime, '--self-contained', 'true', '-m:1', '-nr:false',
    '-p:PublishTrimmed=false', '-p:PublishAot=false', '-p:PublishSingleFile=false', '-o', $destination)
if ($PSBoundParameters.ContainsKey('Version')) { $publishArgs += "-p:Version=$Version" }
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw '宿主发布失败' }
