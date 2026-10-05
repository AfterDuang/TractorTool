#requires -Version 7.4
# Read-only checks against a clean, complete distribution; no application or installer is started.
param([Parameter(Mandatory)][string]$AssetsDir)
$ErrorActionPreference = 'Stop'
$verify = Join-Path (Split-Path $PSScriptRoot) 'scripts/Verify-Distribution.ps1'
$release = Get-Content -LiteralPath (Join-Path $AssetsDir 'release.json') -Raw | ConvertFrom-Json
$tag = "v$($release.version)"
$commit = $release.sourceCommit
& $verify -AssetsDir $AssetsDir -ExpectedTag $tag -ExpectedCommit $commit
foreach ($invalid in @(
    @{ ExpectedTag = $tag + '-mismatch'; ExpectedCommit = $commit },
    @{ ExpectedTag = $tag; ExpectedCommit = ('0' * 40) },
    @{ ExpectedTag = $tag; ExpectedCommit = '' }
)) {
    $rejected = $false
    try { & $verify -AssetsDir $AssetsDir @invalid }
    catch {
        if (!$_.Exception.Message.StartsWith('Release 身份不匹配：')) { throw }
        $rejected = $true
    }
    if (!$rejected) { throw 'Release 校验未拒绝错误标签或提交。' }
}
Write-Output 'Release 身份静态回归通过；未运行或发布应用。'
