#requires -Version 7.4
# Compile and run a text-only Inno fixture with the production install/recovery code.
param([Parameter(Mandatory)][string]$CompilerPath)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw '此检查需要 Windows 和 Inno Setup 编译器。' }
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$identity = [guid]::NewGuid().ToString('N')
$tempParent = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
$scratch = Join-Path $tempParent "TractorTool-installer-checks-$identity"
$null = New-Item -ItemType Directory -Path $scratch
$seed = 'a' * 64

function Test-Install([string]$Name, [ValidateSet('seed','keep','reject')][string]$Expected,
    [string[]]$Files = @(), [string]$Marker, [string]$Pending) {
    $target = Join-Path $scratch $Name
    $null = New-Item -ItemType Directory -Path $target
    $original = @{}
    foreach ($file in $Files) { $original[$file] = if ($file -eq 'installation.ini') { $installationIni } else { 'preserve' } }
    if ($Marker) { $original['.plugin-seed'] = $Marker }
    if ($Pending) { $original['.plugin-seed.new'] = $Pending }
    foreach ($file in $original.Keys) {
        $path = Join-Path $target $file
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllText($path, $original[$file])
    }
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('"/DIR=' + $target + '"'),
        ('"/LOG=' + (Join-Path $scratch "$Name.log") + '"'))
    $result = Start-Process -FilePath (Join-Path $scratch 'probe.exe') -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if (($result.ExitCode -eq 0) -ne ($Expected -ne 'reject')) { throw "$Name 返回意外退出码 $($result.ExitCode)" }
    if ($Expected -eq 'reject') {
        if (@(Get-ChildItem -LiteralPath $target -Recurse -File -Force).Count -ne $original.Count) { throw "$Name 改动了文件清单" }
        foreach ($file in $original.Keys) {
            if ([IO.File]::ReadAllText((Join-Path $target $file)) -cne $original[$file]) { throw "$Name 改动了 $file" }
        }
    } else {
        $expectedMarker = if ($Expected -eq 'seed') { $seed } else { "keep:$seed" }
        if ([IO.File]::ReadAllText((Join-Path $target 'first-file-marker.txt')) -cne $expectedMarker) { throw "$Name 首个宿主文件缺少事务屏障" }
        if ([IO.File]::ReadAllText((Join-Path $target 'host.txt')) -cne 'payload') { throw "$Name 未安装宿主载荷" }
        if ((Test-Path -LiteralPath (Join-Path $target 'Plugins/Fixture/sample.txt')) -ne ($Expected -eq 'seed')) { throw "$Name 的插件安装决策错误" }
        if ((Test-Path -LiteralPath (Join-Path $target '.plugin-seed')) -or (Test-Path -LiteralPath (Join-Path $target '.plugin-seed.new'))) { throw "$Name 安装标记未收尾" }
        if ($original.ContainsKey('Plugins/retained.txt') -and [IO.File]::ReadAllText((Join-Path $target 'Plugins/retained.txt')) -cne 'preserve') { throw "$Name 改动了既有插件" }
    }
    "PASS $Name"
}

try {
    $packaging = Join-Path (Split-Path $PSScriptRoot) 'packaging'
    $template = [IO.File]::ReadAllText((Join-Path $packaging 'TractorTool.iss'))
    $productionIni = [IO.File]::ReadAllText((Join-Path $packaging 'installation.ini'))
    if ($productionIni -notmatch '(?m)^Id=([\w-]+)\s*$') { throw '无法读取生产安装身份。' }
    $productionId = $Matches[1]
    $installationIni = $productionIni.Replace($productionId, $identity)
    $start = $template.IndexOf('[Code]', [StringComparison]::Ordinal)
    if ($start -lt 0 -or !$template.Substring($start).Contains("'$productionId'")) { throw '安装器代码缺少与 installation.ini 一致的安装身份。' }
    $code = $template.Substring($start).Replace($productionId, $identity).Replace('Local\TractorTool.', "Local\TractorToolFixture-$identity.")
    $fixture = @'
[Setup]
AppName=TractorTool recovery check
AppId=TractorToolFixture-{#FixtureId}
AppVersion=1.0.0
PrivilegesRequired=lowest
Uninstallable=no
CreateUninstallRegKey=no
UsePreviousAppDir=no
DefaultDirName={tmp}\TractorToolFixture-{#FixtureId}
OutputBaseFilename=probe
CloseApplications=no
RestartApplications=no
AppMutex=Local\TractorToolFixture-{#FixtureId}.Application.Active
SetupMutex=Local\TractorToolFixture-{#FixtureId}.Installer.Active
[Files]
Source: "installation.ini"; DestDir: "{app}"; Flags: ignoreversion; BeforeInstall: ObserveSeed
Source: "sample.txt"; DestDir: "{app}"; DestName: "host.txt"; Flags: ignoreversion
Source: "sample.txt"; DestDir: "{app}\Plugins\Fixture"; Check: SeedPlugins
'@
    $observe = @'
procedure ObserveSeed;
var
  Marker: AnsiString;
begin
  if not LoadStringFromFile(ExpandConstant('{app}\.plugin-seed'), Marker) or
    not SaveStringToFile(ExpandConstant('{app}\first-file-marker.txt'), Marker, False) then
    RaiseException('Missing first-file marker');
end;
'@
    $iss = Join-Path $scratch 'probe.iss'
    Set-Content -LiteralPath $iss -Value @($fixture, $code, $observe) -Encoding utf8BOM
    [IO.File]::WriteAllText((Join-Path $scratch 'installation.ini'), $installationIni)
    [IO.File]::WriteAllText((Join-Path $scratch 'sample.txt'), 'payload')
    & $compiler '/Q' "/DFixtureId=$identity" "/DSeedHash=$seed" '/DAppNumericVersion=1.0.0' "/O$scratch" $iss *> (Join-Path $scratch 'compiler.log')
    if ($LASTEXITCODE -ne 0) { throw "夹具编译失败，退出码 $LASTEXITCODE" }

    Test-Install new seed
    Test-Install host-interrupted seed @('host.txt') -Marker $seed
    Test-Install plugin-interrupted seed @('host.txt', 'Plugins/Fixture/partial') -Marker $seed
    Test-Install retained keep @('Plugins/retained.txt')
    Test-Install retained-interrupted keep @('host.txt', 'Plugins/retained.txt') -Marker "keep:$seed"
    Test-Install unowned reject @('unrelated.txt')
    Test-Install wrong-zip reject @('host.txt') -Marker ('b' * 64)
    Test-Install foreign-temporary reject -Pending 'unrelated-content'
    Test-Install pending-seed seed -Pending $seed
    Test-Install pending-keep keep @('Plugins/retained.txt') -Pending "keep:$seed"
    Test-Install mismatched-mode reject @('Plugins/retained.txt') -Pending $seed
    Test-Install managed-foreign-temporary reject @('installation.ini') -Pending ('b' * 64)
} catch {
    throw "安装器恢复检查失败。现场与日志保存在 $scratch。$($_.Exception.Message)"
}

$actual = (Resolve-Path -LiteralPath $scratch).Path
if ($actual -ne $scratch -or [IO.Path]::GetDirectoryName($actual) -ne $tempParent) { throw "临时目录路径异常，保留 $scratch" }
Remove-Item -LiteralPath $actual -Recurse -Force
