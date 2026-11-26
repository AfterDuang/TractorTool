# The host emits its SDK inventory into runtimeconfig. Verify it against the target's managed assets
# before using it to classify DLLs. Older hosts reserved the whole Avalonia.* family.
function Get-PluginSdkAssemblyPolicy($RuntimeConfig, $HostDeps) {
    $inventory = $RuntimeConfig.runtimeOptions.configProperties.'TractorTool.PluginSdk.ReservedAssemblies'
    if ($null -eq $inventory) {
        $product = @($HostDeps.libraries.Keys | Where-Object { $_ -like 'TractorTool/*' })
        if ($product.Count -eq 1 -and [semver]($product[0].Split('/')[1]) -lt [semver]'1.1.0') { return $null }
        throw '目标宿主缺少共享程序集清单。'
    }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in 'TractorTool', 'TractorTool.Core', 'TractorTool.Contracts', 'TractorTool.UI') { [void]$expected.Add($name) }
    $target = $HostDeps.targets[$HostDeps.runtimeTarget.name]
    if (!$target) { throw '目标宿主缺少托管运行时资产。' }
    foreach ($library in $target.Values) {
        foreach ($asset in $library.runtime.Keys) {
            $file = [IO.Path]::GetFileName($asset)
            if ($file -match '^(Avalonia(\..+)?|MicroCom\.Runtime|FluentAvalonia)\.dll$' -and $file -notlike '*.resources.dll') {
                [void]$expected.Add([IO.Path]::GetFileNameWithoutExtension($file))
            }
        }
    }
    $names = $inventory -split '\|'
    if ($expected.Count -ne $names.Count -or !$expected.SetEquals([string[]]$names)) { throw '目标宿主共享程序集清单与托管运行时资产不一致。' }
    return ,$expected
}

function Assert-PluginSdkFile($Policy, [string]$Path, [string]$UiApiVersion) {
    if (!$Path.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) { return }
    $name = [IO.Path]::GetFileNameWithoutExtension($Path)
    if ($null -eq $Policy) {
        if ($name -match '^(TractorTool(\.Core|\.Contracts|\.UI)?|Avalonia(\..*)?|FluentAvalonia|MicroCom\.Runtime)$') {
            throw "旧版宿主禁止插件携带程序集：$Path"
        }
        return
    }
    if ($Policy.Contains($name)) { throw "插件夹带共享程序集：$Path" }
    if ($name.StartsWith('Avalonia.', [StringComparison]::OrdinalIgnoreCase) -and ($UiApiVersion -as [version]) -lt [version]'1.6') {
        throw "私有 Avalonia 扩展 $Path 需要在清单声明 UI API 1.6 或更高。"
    }
}

# A declared Python environment is prepared in DataRoot; its interpreter and venv are not plugin payload.
function Assert-PluginPythonFile($Manifest, [string]$Path) {
    if ($Manifest.PythonEnvironments.Count -eq 0) { return }
    if ([IO.Path]::GetFileName($Path.Replace('\', '/')) -in @('pyvenv.cfg', 'python.exe', 'pythonw.exe', 'uv.exe', 'uvw.exe', 'uvx.exe')) {
        throw "Python 插件不能携带受管理解释器、uv 或已安装环境：$Path"
    }
}
