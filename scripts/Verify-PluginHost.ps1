param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$run = Join-Path $repo ('artifacts/plugin-host-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6))
$publish = Join-Path $run 'host'
New-Item -ItemType Directory -Path $run -Force | Out-Null
function Invoke-Recorded([string]$Name, [scriptblock]$Action) {
    & $Action 2>&1 | Tee-Object -FilePath (Join-Path $run ($Name + '.txt'))
    if ($LASTEXITCODE -ne 0) { throw "$Name failed: $LASTEXITCODE" }
}
Invoke-Recorded 'environment' { dotnet --info }
Invoke-Recorded 'build' { dotnet build (Join-Path $repo 'TractorTool.slnx') -c $Configuration -m:1 -nr:false }
Invoke-Recorded 'core-checks' { dotnet run --project (Join-Path $repo 'tests/TractorTool.Checks') -c $Configuration --no-build }
& (Join-Path $PSScriptRoot 'Publish-Host.ps1') -Output $publish -Configuration $Configuration 2>&1 | Tee-Object (Join-Path $run 'publish.txt')
$before = @(Get-ChildItem -LiteralPath $publish -Recurse -File | ForEach-Object { [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($publish, $_.FullName); SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
$before | ConvertTo-Json | Set-Content (Join-Path $run 'host-hashes-before.json')
# Local development may have built the plugin already. Rebuild using ONLY the published SDK after hashing the host.
$pluginPackage = Join-Path $run 'plugin-build'
# This fixture intentionally has no MVVM dependency; exercise the same packaging entry as real plugins.
Invoke-Recorded 'plugin-build' { & (Join-Path $PSScriptRoot 'Build-Plugin.ps1') -Project (Join-Path $repo 'tests/IndependentPlugin/IndependentPlugin.csproj') -HostSdkDir $publish -Output $pluginPackage }
$pluginOutput = Join-Path $pluginPackage 'files'
$pluginManifest = & (Join-Path $PSScriptRoot 'Read-PluginManifest.ps1') -Path (Join-Path $pluginOutput 'plugin.json')
$install = Join-Path $publish "Plugins/$($pluginManifest.id)/$($pluginManifest.version)"
New-Item -ItemType Directory -Path $install -Force | Out-Null
Get-ChildItem -LiteralPath $pluginOutput | Copy-Item -Destination $install -Recurse
Invoke-Recorded 'integration-build' { dotnet build (Join-Path $repo 'tests/TractorTool.IntegrationChecks/TractorTool.IntegrationChecks.csproj') -t:Rebuild -c $Configuration -m:1 -nr:false "-p:HostSdkDir=$publish" }
Invoke-Recorded 'integration-checks' { dotnet run --project (Join-Path $repo 'tests/TractorTool.IntegrationChecks') -c $Configuration --no-build -- $publish (Join-Path $run 'integration') }
$after = @($before | ForEach-Object { [pscustomobject]@{ Path = $_.Path; SHA256 = (Get-FileHash -LiteralPath (Join-Path $publish $_.Path) -Algorithm SHA256).Hash } })
$after | ConvertTo-Json | Set-Content (Join-Path $run 'host-hashes-after.json')
$difference = Compare-Object $before $after -Property Path,SHA256
if ($difference) { $difference | Out-String | Write-Error; throw '宿主文件发生变化' }
"PASS: all $($before.Count) published host files unchanged after independent plugin build/install/test." | Tee-Object (Join-Path $run 'hash-result.txt')
"Evidence: $run"
"UI command: & '$publish/TractorTool.exe' --profile-root '$run/ui-profile'"
