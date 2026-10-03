param(
    [Parameter(Mandatory)][string]$HostSdkDir,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$hostSdk = [IO.Path]::GetFullPath($HostSdkDir)
if (!(Test-Path -LiteralPath (Join-Path $hostSdk 'TractorTool.UI.dll'))) { throw 'HostSdkDir 必须指向已经发布的宿主。' }
$destination = [IO.Path]::GetFullPath($Output)
if ((Test-Path -LiteralPath $destination) -and (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)) { throw '插件输出目录必须为空，避免覆盖旧包。' }
$project = Join-Path $repo 'Plugins/TractorTool.Plugins.ClipboardHistory/TractorTool.Plugins.ClipboardHistory.csproj'
$manifest = Get-Content -LiteralPath (Join-Path (Split-Path $project) 'plugin.json') -Raw | ConvertFrom-Json
$files = Join-Path $destination 'files'
# Rebuild with normal restore when switching from source references to published DLLs.
dotnet build $project -t:Rebuild -c Release -m:1 -nr:false "-p:HostSdkDir=$hostSdk" "-p:Version=$($manifest.version)" -o $files
if ($LASTEXITCODE -ne 0) { throw '剪贴板插件构建失败。' }
$shared = @(Get-ChildItem -LiteralPath $files -Recurse -Filter *.dll | Where-Object {
    $_.BaseName -match '^(TractorTool(\.Core|\.Contracts|\.UI)?|Avalonia(\..*)?|FluentAvalonia|MicroCom\.Runtime)$'
})
if ($shared.Count) { throw '插件输出包含宿主/共享 SDK 的副本。' }
# Resolve licenses from the exact packages used by this restore, not a machine-wide guessed path.
$assets = Get-Content -LiteralPath (Join-Path (Split-Path $project) 'obj/project.assets.json') -Raw | ConvertFrom-Json
$toolkit = $assets.libraries.'CommunityToolkit.Mvvm/8.4.2'.path
$licenseRoot = @($assets.packageFolders.PSObject.Properties.Name | Where-Object { Test-Path -LiteralPath (Join-Path $_ "$toolkit/License.md") })[0]
if (!$licenseRoot) { throw '未找到本次还原包的 MVVM 许可。' }
$licenses = Join-Path $files 'Licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $licenseRoot "$toolkit/License.md") -Destination (Join-Path $licenses 'CommunityToolkit.Mvvm.md')
Copy-Item -LiteralPath (Join-Path $licenseRoot "$toolkit/ThirdPartyNotices.txt") -Destination $licenses
Copy-Item -LiteralPath (Join-Path $repo 'Plugins/TractorTool.Plugins.ClipboardHistory/Licenses/CsWinRT.txt') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $repo 'Plugins/TractorTool.Plugins.ClipboardHistory/Licenses/WindowsSDK.rtf') -Destination $licenses
$manifest = Get-Content -LiteralPath (Join-Path $files 'plugin.json') -Raw | ConvertFrom-Json
$archive = Join-Path $destination "$($manifest.id)-$($manifest.version).zip"
[IO.Compression.ZipFile]::CreateFromDirectory($files, $archive)
(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
Write-Output "插件 ZIP：$archive"
