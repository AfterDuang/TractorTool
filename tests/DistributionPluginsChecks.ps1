#requires -Version 7.4
# Temporary source/ZIP fixtures only; no compilation, assembly loading, network or application execution.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$readPlugins = Join-Path $repo 'scripts/Get-DistributionPlugins.ps1'
$verify = Join-Path $repo 'scripts/Verify-Distribution.ps1'
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$scratch = Join-Path $tempParent ('TractorTool-distribution-checks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
function Write-Json([string]$Path, $Value) {
    ConvertTo-Json -InputObject $Value -Depth 10 | Set-Content -LiteralPath $Path -Encoding utf8
}
function Expect-Rejection([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if (!$_.Exception.Message.Contains($Message)) { throw }
        return
    }
    throw "应拒绝但未拒绝：$Message"
}
function New-DistributionFixture([string]$Name, [int]$Count, [switch]$Duplicate, [switch]$Unlisted) {
    $fixture = Join-Path $scratch $Name
    $layout = Join-Path $fixture 'Bundle'
    $assets = Join-Path $fixture 'assets'
    New-Item -ItemType Directory -Path $assets, (Join-Path $layout 'Licenses') | Out-Null
    foreach ($file in @('TractorTool.exe', 'TractorTool.dll', 'TractorTool.Contracts.dll', 'TractorTool.UI.dll', 'THIRD-PARTY-NOTICES.md', 'Licenses/dependencies.json')) {
        Set-Content -LiteralPath (Join-Path $layout $file) -Value 'Static fixture, not executable.'
    }
    $records = @()
    for ($i = 0; $i -lt $Count; $i++) {
        $id = "Example.Plugin$i"
        $path = "Plugins/$id/1.2.3"
        $directory = Join-Path $layout $path
        New-Item -ItemType Directory -Path $directory | Out-Null
        Write-Json (Join-Path $directory 'plugin.json') @{ id = $id; version = '1.2.3' }
        Set-Content -LiteralPath (Join-Path $directory "$id.dll") -Value 'Static fixture, not an assembly.'
        $archive = "$id-1.2.3.zip"
        [IO.Compression.ZipFile]::CreateFromDirectory($directory, (Join-Path $assets $archive))
        $records += @{ id = $id; version = '1.2.3'; path = $path; archive = $archive }
    }
    if ($Duplicate) { $records += $records[0] }
    if ($Unlisted) {
        New-Item -ItemType Directory -Path (Join-Path $layout 'Plugins/Extra/1.0.0') | Out-Null
        Set-Content -LiteralPath (Join-Path $layout 'Plugins/Extra/1.0.0/extra.txt') -Value 'Not declared.'
    }
    Write-Json (Join-Path $assets 'release.json') @{ version = '0.1.0'; archive = 'Bundle.zip'; bundleDirectory = 'Bundle'; plugins = $records }
    Copy-Item -LiteralPath (Join-Path $assets 'release.json') -Destination $layout
    $inventory = @(Get-ChildItem -LiteralPath $layout -Recurse -File | ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath($layout, $_.FullName).Replace('\', '/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    Write-Json (Join-Path $layout 'files.json') $inventory
    [IO.Compression.ZipFile]::CreateFromDirectory($layout, (Join-Path $assets 'Bundle.zip'), [IO.Compression.CompressionLevel]::Optimal, $true)
    Get-ChildItem -LiteralPath $assets -File | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash, $_.Name
    } | Set-Content -LiteralPath (Join-Path $assets 'SHA256SUMS') -Encoding ascii
    return $assets
}
try {
    if (@(& $readPlugins -Repository $scratch).Count -ne 0) { throw '无插件目录时应为空。' }
    foreach ($directory in @('tests/Fixture', 'Plugins/Support/bin', 'Plugins/Support/obj')) {
        $path = Join-Path $scratch $directory
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $path 'ignored.csproj') -Value '<Project />'
        Write-Json (Join-Path $path 'plugin.json') @{ schemaVersion = 1; id = 'Ignored.Plugin'; version = '1.0.0' }
    }
    Set-Content -LiteralPath (Join-Path $scratch 'Plugins/Support/helper.csproj') -Value '<Project />'
    $projects = @('Plugins/Alpha/custom.csproj', 'Plugins/Beta/different.csproj', 'Plugins/Gamma/third.csproj')
    $created = 0
    foreach ($count in @(0, 1, 3)) {
        for ($i = $count - 1; $i -ge $created; $i--) {
            $project = Join-Path $scratch $projects[$i]
            New-Item -ItemType Directory -Path (Split-Path $project) | Out-Null
            Set-Content -LiteralPath $project -Value '<Project />'
            Write-Json (Join-Path (Split-Path $project) 'plugin.json') @{ schemaVersion = 1; id = "Example.Tool$i"; version = '1.2.3' }
        }
        $created = $count
        $selected = @(& $readPlugins -Repository $scratch)
        if ($selected.Count -ne $count) { throw "插件数量错误：$count" }
        for ($i = 0; $i -lt $count; $i++) {
            if ($selected[$i].Id -ne "Example.Tool$i" -or $selected[$i].Version -ne '1.2.3' -or
                $selected[$i].Project -ne [IO.Path]::GetFullPath((Join-Path $scratch $projects[$i]))) { throw '未按目录名排序、实际工程名或插件身份发现。' }
        }
        & $verify -AssetsDir (New-DistributionFixture "valid-$count" $count)
    }
    $extraProject = Join-Path $scratch 'Plugins/Alpha/extra.csproj'
    Set-Content -LiteralPath $extraProject -Value '<Project />'
    Expect-Rejection { & $readPlugins -Repository $scratch } '有且仅有一个 csproj'
    Remove-Item -LiteralPath $extraProject
    $firstProject = Join-Path $scratch $projects[0]
    Move-Item -LiteralPath $firstProject -Destination "$firstProject.disabled"
    Expect-Rejection { & $readPlugins -Repository $scratch } '有且仅有一个 csproj'
    Move-Item -LiteralPath "$firstProject.disabled" -Destination $firstProject
    $secondManifest = Join-Path (Split-Path (Join-Path $scratch $projects[1])) 'plugin.json'
    Write-Json $secondManifest @{ schemaVersion = 1; id = 'example.tool0'; version = '1.2.3' }
    Expect-Rejection { & $readPlugins -Repository $scratch } 'ID 重复'
    Write-Json $secondManifest @{ schemaVersion = 1; id = '../escape'; version = '1.2.3' }
    Expect-Rejection { & $readPlugins -Repository $scratch } '清单身份无效'
    Write-Json $secondManifest @{ schemaVersion = 1; id = 'Example.Tool1'; version = '1.2.3-beta' }
    Expect-Rejection { & $readPlugins -Repository $scratch } '清单身份无效'
    Set-Content -LiteralPath $secondManifest -Value '{broken'
    Expect-Rejection { & $readPlugins -Repository $scratch } 'JSON'
    $duplicateAssets = New-DistributionFixture 'duplicate' 1 -Duplicate
    Expect-Rejection { & $verify -AssetsDir $duplicateAssets } '发布插件记录重复'
    $unlistedAssets = New-DistributionFixture 'unlisted' 1 -Unlisted
    Expect-Rejection { & $verify -AssetsDir $unlistedAssets } '未登记的插件文件'
    Write-Output '随包插件自动发现与 ZIP 静态回归通过；未编译或运行应用。'
} finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $tempParent -or
        [IO.Path]::GetFileName($resolved) -notmatch '^TractorTool-distribution-checks-[0-9a-f]{32}$') { throw '拒绝清理临时测试目录以外的路径。' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
