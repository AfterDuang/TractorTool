#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$HostSdkDir,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$hostSdk = [IO.Path]::GetFullPath($HostSdkDir)
foreach ($assembly in @('TractorTool.Contracts.dll', 'TractorTool.UI.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $hostSdk $assembly) -PathType Leaf)) { throw 'HostSdkDir 必须指向已经发布的宿主。' }
}
$destination = [IO.Path]::GetFullPath($Output)
if ((Test-Path -LiteralPath $destination) -and (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)) {
    throw '插件输出目录必须为空，避免覆盖旧包。'
}
$projectRoot = Split-Path ([IO.Path]::GetFullPath($Project))
$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'plugin.json') -Raw | ConvertFrom-Json
$files = Join-Path $destination 'files'
# Normal restore is required when switching between source SDK references and published DLLs.
dotnet build $Project -t:Rebuild -c Release -m:1 -nr:false "-p:HostSdkDir=$hostSdk" "-p:Version=$($manifest.version)" -o $files
if ($LASTEXITCODE -ne 0) { throw '插件构建失败。' }
$shared = @(Get-ChildItem -LiteralPath $files -Recurse -Filter *.dll | Where-Object {
    $_.BaseName -match '^(TractorTool(\.Core|\.Contracts|\.UI)?|Avalonia(\..*)?|FluentAvalonia|MicroCom\.Runtime)$'
})
if ($shared.Count) { throw '插件输出包含宿主/共享 SDK 的副本。' }
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$toolkit = @($assets.libraries.Keys | Where-Object { $_ -like 'CommunityToolkit.Mvvm/*' })
if ($toolkit.Count -ne 1) { throw '未找到唯一的 MVVM 还原版本。' }
$packagePath = $assets.libraries[$toolkit[0]].path
$packageRoot = @($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $packagePath } |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'License.md') }) | Select-Object -First 1
if (!$packageRoot) { throw '未找到本次还原包的 MVVM 许可。' }
$licenses = Join-Path $files 'Licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $packageRoot 'License.md') -Destination (Join-Path $licenses 'CommunityToolkit.Mvvm.md')
Copy-Item -LiteralPath (Join-Path $packageRoot 'ThirdPartyNotices.txt') -Destination $licenses
$privateLicenses = Join-Path $projectRoot 'Licenses'
if (Test-Path -LiteralPath $privateLicenses) {
    Get-ChildItem -LiteralPath $privateLicenses | Copy-Item -Destination $licenses -Recurse
}
$archive = Join-Path $destination "$($manifest.id)-$($manifest.version).zip"
[IO.Compression.ZipFile]::CreateFromDirectory($files, $archive)
(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
Write-Output "插件 ZIP：$archive"
