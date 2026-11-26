#requires -Version 7.4
# Temporary source/ZIP fixtures only; no compilation, assembly loading, network or application execution.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$readPlugins = Join-Path $repo 'scripts/Get-DistributionPlugins.ps1'
$verify = Join-Path $repo 'scripts/Verify-Distribution.ps1'
$readManifest = Join-Path $repo 'scripts/Read-PluginManifest.ps1'
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
function New-DistributionFixture([string]$Name, [int]$Count, [switch]$Duplicate, [switch]$Unlisted, [switch]$MisCasedId,
    [hashtable]$Dependencies = @{}, [string[]]$ExtraPaths = @(), [string[]]$HostNativeFiles = @(), [string]$ApiVersion = '1.8',
    [hashtable]$PythonEnvironments = @{}, [hashtable]$PythonInputs = @{}) {
    $fixture = Join-Path $scratch $Name
    $layout = Join-Path $fixture 'Bundle'
    $assets = Join-Path $fixture 'assets'
    New-Item -ItemType Directory -Path $assets, (Join-Path $layout 'Licenses') | Out-Null
    $sdkNames = @('TractorTool', 'TractorTool.Core', 'TractorTool.Contracts', 'TractorTool.UI', 'Avalonia.Controls')
    Write-Json (Join-Path $layout 'TractorTool.runtimeconfig.json') @{ runtimeOptions = @{ configProperties = @{
        'TractorTool.PluginSdk.ReservedAssemblies' = $sdkNames -join '|'
    } } }
    $hostTarget = @{
        'Avalonia/12.1.6' = @{ runtime = @{ 'lib/net10.0/Avalonia.Controls.dll' = @{} } }
    }
    if ($HostNativeFiles.Count -gt 0) {
        $native = @{}
        foreach ($path in $HostNativeFiles) { $native[$path] = @{} }
        $hostTarget['Host.Native/1.0.0'] = @{ native = $native }
    }
    Write-Json (Join-Path $layout 'TractorTool.deps.json') @{ runtimeTarget = @{ name = 'test' }; targets = @{ test = $hostTarget } }
    foreach ($file in @(($sdkNames | ForEach-Object { "$_.dll" })) + @('TractorTool.exe', 'THIRD-PARTY-NOTICES.md', 'Licenses/dependencies.json')) {
        Set-Content -LiteralPath (Join-Path $layout $file) -Value 'Static fixture, not executable.'
    }
    foreach ($relative in $ExtraPaths) {
        $file = Join-Path $layout $relative
        New-Item -ItemType Directory -Path (Split-Path $file) -Force | Out-Null
        [IO.File]::WriteAllText($file, 'Fixture payload')
    }
    $records = @()
    for ($i = 0; $i -lt $Count; $i++) {
        $id = "Example.Plugin$i"
        $path = "Plugins/$id/1.2.3"
        $directory = Join-Path $layout $path
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $manifest = @{ id = $id; version = '1.2.3'; apiVersion = $ApiVersion; uiApiVersion = '1.6' }
        if ($MisCasedId) { $manifest.id = $id.ToLowerInvariant() }
        if ($Dependencies.ContainsKey($i)) { $manifest['DePeNdEnCiEs'] = $Dependencies[$i] }
        if ($PythonEnvironments.ContainsKey($i)) { $manifest['PythonEnvironments'] = $PythonEnvironments[$i] }
        Write-Json (Join-Path $directory 'plugin.json') $manifest
        foreach ($inputPath in $PythonInputs.Keys) {
            $outputPath = Join-Path $directory $inputPath
            New-Item -ItemType Directory -Path (Split-Path $outputPath) -Force | Out-Null
            [IO.File]::WriteAllText($outputPath, $PythonInputs[$inputPath], [Text.UTF8Encoding]::new($false))
        }
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
    Write-Json (Join-Path $assets 'release.json') @{ version = '0.1.0'; runtime = 'win-x64'; archive = 'Bundle.zip'; bundleDirectory = 'Bundle'; plugins = $records }
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
    & $verify -AssetsDir (New-DistributionFixture 'private-ui-extension' 1 -ExtraPaths 'Plugins/Example.Plugin0/1.2.3/Avalonia.Independent.dll')
    $sharedDll = New-DistributionFixture 'shared-ui-copy' 1 -ExtraPaths 'Plugins/Example.Plugin0/1.2.3/nested/AVALONIA.CONTROLS.DLL'
    Expect-Rejection { & $verify -AssetsDir $sharedDll } '插件夹带共享程序集'
    Expect-Rejection { & $verify -AssetsDir $duplicateAssets } '发布插件记录重复'
    $unlistedAssets = New-DistributionFixture 'unlisted' 1 -Unlisted
    Expect-Rejection { & $verify -AssetsDir $unlistedAssets } '未登记的插件文件'
    Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture 'case-mismatch' 1 -MisCasedId) } 'ID 或版本不一致'
    # The first plugin references a recipe supplied by the second. The third requires a different version.
    $tools = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $tools.Add('PSObject', @{ pAtH = 'upstream\bin\custom-probe.exe' })
    $tools.Add('Values', @{ path = 'values-probe.exe' })
    $tools.Add('Keys', @{ path = 'keys-probe.exe' })
    $tools.Add('Count', @{ path = 'count-probe.exe' })
    $tools.Add('RG', @{ path = 'upper-probe.exe' })
    $tools.Add('rg', @{ path = 'lower-probe.exe' })
    $tools.Add('GetEnumerator', @{ path = 'enumerator-probe.exe' })
    $download = @{ RiD = 'win-x64'; sHa256 = 'ab' * 32; ToOlS = $tools }
    $deps = @{
        0 = @(@{ id = 'Example.External'; version = '1.0.0' })
        1 = @(@{ ID = 'Example.External'; vErSiOn = '1.0.0'; DoWnLoAdS = @($download) })
        2 = @(@{ id = 'Example.External'; version = '2.0.0' })
    }
    $consumerPath = 'Plugins/Example.Plugin0/1.2.3/tools/CUSTOM-PROBE.EXE'
    $privatePath = 'Plugins/Example.Plugin2/1.2.3/assets/custom-probe.exe'
    & $verify -AssetsDir (New-DistributionFixture 'private-tool-name' 3 -Dependencies $deps -ExtraPaths $privatePath)
    foreach ($path in @('bin/Custom-Probe.exe', $consumerPath, 'Plugins/Example.Plugin1/1.2.3/custom-probe.exe',
        'upper-probe.exe', 'lower-probe.exe', 'values-probe.exe', 'keys-probe.exe', 'count-probe.exe', 'enumerator-probe.exe')) {
        $case = 'external-' + [guid]::NewGuid().ToString('N')
        Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture $case 3 -Dependencies $deps -ExtraPaths $path) } "外部依赖入口：$path"
    }
    Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture 'host-native-cannot-exempt-tool' 3 -Dependencies $deps `
        -ExtraPaths 'custom-probe.exe' -HostNativeFiles 'runtimes/win-x64/native/custom-probe.exe') } '外部依赖入口：custom-probe.exe'

    # Native packages use the same cross-plugin reference lookup, including ordinary dictionary-key names.
    $libraries = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $libraries.Add('SDL', 'runtime/native-probe.dll')
    $libraries.Add('Values', 'values-probe.dll')
    $libraries.Add('Keys', 'keys-probe.dll')
    $libraries.Add('Count', 'count-probe.dll')
    $libraries.Add('GetEnumerator', 'enumerator-probe.dll')
    $nativeDownload = @{ rid = 'win-x64'; sha256 = 'cd' * 32; kind = 'nativeLibrary'; libraries = $libraries }
    $nativeDeps = @{
        0 = @(@{ id = 'Example.Native'; version = '1.0.0' })
        1 = @(@{ id = 'Example.Native'; version = '1.0.0'; downloads = @($nativeDownload) })
        2 = @(@{ id = 'Example.Native'; version = '2.0.0' })
    }
    & $verify -AssetsDir (New-DistributionFixture 'native-package-declaration' 3 -Dependencies $nativeDeps)
    & $verify -AssetsDir (New-DistributionFixture 'native-current-api' 3 -Dependencies $nativeDeps -ApiVersion '1.9')
    & $verify -AssetsDir (New-DistributionFixture 'unrelated-private-native' 3 -Dependencies $nativeDeps `
        -ExtraPaths 'Plugins/Example.Plugin2/1.2.3/native-probe.dll')
    foreach ($path in @('native-probe.dll', 'Plugins/Example.Plugin0/1.2.3/native/NATIVE-PROBE.DLL',
        'Plugins/Example.Plugin1/1.2.3/native-probe.dll', 'values-probe.dll', 'keys-probe.dll', 'count-probe.dll', 'enumerator-probe.dll')) {
        $case = 'external-native-' + [guid]::NewGuid().ToString('N')
        Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture $case 3 -Dependencies $nativeDeps -ExtraPaths $path) } "外部依赖入口：$path"
    }
    Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture 'native-old-api' 3 -Dependencies $nativeDeps -ApiVersion '1.7') } '原生运行库需要 API 1.8'
    $badNativeDeps = @{ 0 = @(@{ id = 'Example.Native'; version = '1.0.0'; downloads = @(@{
        rid = 'win-x64'; sha256 = 'cd' * 32; kind = 'nativeLibrary'; libraries = @{ entry = '../foo.dll' }
    }) }) }
    Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture 'native-unsafe-path' 1 -Dependencies $badNativeDeps) } '安全的包内相对路径：../foo.dll'

    # A declared host native asset may coexist at the host root, but not in its consumer plugin or a nested folder.
    $skiaDownload = @{ rid = 'win-x64'; sha256 = 'ef' * 32; kind = 'nativeLibrary'; libraries = @{ skia = 'libSkiaSharp.dll' } }
    $skiaDeps = @{ 0 = @(@{ id = 'Example.Skia'; version = '1.0.0'; downloads = @($skiaDownload) }) }
    $skiaAsset = 'runtimes/win-x64/native/libSkiaSharp.dll'
    & $verify -AssetsDir (New-DistributionFixture 'host-owned-native' 1 -Dependencies $skiaDeps `
        -ExtraPaths 'libSkiaSharp.dll' -HostNativeFiles $skiaAsset)
    Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture 'unknown-host-native' 1 -Dependencies $skiaDeps `
        -ExtraPaths 'libSkiaSharp.dll') } '外部依赖入口：libSkiaSharp.dll'
    foreach ($path in @('Plugins/Example.Plugin0/1.2.3/libSkiaSharp.dll', 'nested/libSkiaSharp.dll')) {
        $case = 'host-native-copy-' + [guid]::NewGuid().ToString('N')
        Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture $case 1 -Dependencies $skiaDeps `
            -ExtraPaths @('libSkiaSharp.dll', $path) -HostNativeFiles $skiaAsset) } "外部依赖入口：$path"
    }
    $skiaDownload.sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('Fixture payload')))
    Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture 'host-native-cannot-exempt-archive' 1 -Dependencies $skiaDeps `
        -ExtraPaths 'libSkiaSharp.dll' -HostNativeFiles $skiaAsset) } '外部下载归档：libSkiaSharp.dll'

    $download.sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('Fixture payload')))
    Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture 'renamed-archive' 3 -Dependencies $deps -ExtraPaths 'other.bin') } '外部下载归档：other.bin'
    Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture 'package-store' 0 -ExtraPaths 'Packages/unknown/receipt.json') } '混入用户数据'

    # Lock inputs are plugin files, while interpreters and installed environments stay outside the distribution.
    $projectText = "[project]`nname = 'fixture'`nversion = '1.0.0'`n"
    $lockText = "version = 1`nrevision = 5`n"
    $pythonDeclaration = @{
        id = 'worker'; pythonVersion = '3.13.18'; project = 'Python/pyproject.toml'; lock = 'Python/uv.lock'
        projectSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($projectText)))
        lockSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lockText)))
        source = 'https://pypi.org/simple/'
    }
    $pythonDeclarations = @{ 0 = @($pythonDeclaration) }
    $pythonInputs = @{ 'Python/pyproject.toml' = $projectText; 'Python/uv.lock' = $lockText }
    & $verify -AssetsDir (New-DistributionFixture 'python-locked-inputs' 1 -ApiVersion '1.9' -PythonEnvironments $pythonDeclarations -PythonInputs $pythonInputs)
    foreach ($payload in @('Python/.venv/pyvenv.cfg', 'Python/runtime/PYTHON.EXE', 'Python/tools/uv.exe')) {
        Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture ('python-installed-' + [guid]::NewGuid().ToString('N')) 1 `
            -ApiVersion '1.9' -PythonEnvironments $pythonDeclarations -PythonInputs $pythonInputs -ExtraPaths "Plugins/Example.Plugin0/1.2.3/$payload") } '不能携带受管理解释器'
    }
    Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture 'python-missing-lock' 1 -ApiVersion '1.9' -PythonEnvironments $pythonDeclarations `
        -PythonInputs @{ 'Python/pyproject.toml' = $projectText }) } 'Python 项目或锁文件缺失'
    Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture 'python-mutated-lock' 1 -ApiVersion '1.9' -PythonEnvironments $pythonDeclarations `
        -PythonInputs @{ 'Python/pyproject.toml' = $projectText; 'Python/uv.lock' = $lockText + '# changed' }) } 'Python 项目或锁文件缺失'
    Expect-Rejection { & $verify -AssetsDir (New-DistributionFixture 'python-old-api' 1 -ApiVersion '1.8' -PythonEnvironments $pythonDeclarations -PythonInputs $pythonInputs) } 'Python 环境需要 API 1.9'
    $pythonManifest = @{ apiVersion = '1.9'; pythonEnvironments = @($pythonDeclaration) }
    $pythonDeclaration.project = '../pyproject.toml'
    Expect-Rejection { & $readManifest -Json (ConvertTo-Json $pythonManifest -Depth 8) } '安全的包内相对路径'
    $pythonDeclaration.project = 'Python/pyproject.toml'
    $pythonDeclaration.source = 'https://user:secret@pypi.org/simple/'
    Expect-Rejection { & $readManifest -Json (ConvertTo-Json $pythonManifest -Depth 8) } 'HTTPS Simple API'
    $pythonDeclaration.source = 'https://pypi.org/simple/'
    $pythonManifest.pythonEnvironments += @($pythonDeclaration.Clone())
    $pythonManifest.pythonEnvironments[1].id = 'WORKER'
    Expect-Rejection { & $readManifest -Json (ConvertTo-Json $pythonManifest -Depth 8) } '标识无效或重复'
    Write-Output '随包插件自动发现与 ZIP 静态回归通过；未编译或运行应用。'
} finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $tempParent -or
        [IO.Path]::GetFileName($resolved) -notmatch '^TractorTool-distribution-checks-[0-9a-f]{32}$') { throw '拒绝清理临时测试目录以外的路径。' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
