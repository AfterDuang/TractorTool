#requires -Version 7.0
# Collect metadata and original license texts for the packages in the published runtime graph.
# No network requests and no package code execution. Missing license material fails packaging.
param(
    [Parameter(Mandatory)][string]$AssetsFile,
    [Parameter(Mandatory)][string]$DepsFile,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
$deps = Get-Content -LiteralPath $DepsFile -Raw | ConvertFrom-Json -AsHashtable
$upstreamRoot = Join-Path (Split-Path $PSScriptRoot) 'packaging/licenses'
$sources = @(Get-Content -LiteralPath (Join-Path $upstreamRoot 'sources.json') -Raw | ConvertFrom-Json)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$records = @(foreach ($identity in ($deps.libraries.Keys | Sort-Object)) {
    $library = $deps.libraries[$identity]
    if ($library.type -notin @('package', 'runtimepack')) { continue }
    $package = $identity -replace '^runtimepack\.', ''
    $name, $version = $package.Split('/')
    $packagePath = $package.ToLowerInvariant()
    $packageRoot = @($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $packagePath } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container }) | Select-Object -First 1
    if (!$packageRoot) { throw "找不到实际还原依赖：$package" }
    $nuspec = Get-ChildItem -LiteralPath $packageRoot -Filter *.nuspec -File | Select-Object -First 1
    if (!$nuspec) { throw "缺少 NuGet 元数据：$package" }
    [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
    $license = $metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="license"]')
    $repository = $metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="repository"]')
    $target = Join-Path $Output $package
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath $nuspec.FullName -Destination $target
    $texts = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object {
        $_.Name -match '^(licen[cs]e|copying|ofl|third[-_]?party[-_]?notices)(\..*)?$'
    })
    if ($license -and $license.GetAttribute('type') -eq 'file') {
        $declared = [IO.Path]::GetFullPath((Join-Path $packageRoot $license.InnerText))
        if (![IO.Path]::GetRelativePath($packageRoot, $declared).StartsWith('..') -and (Test-Path -LiteralPath $declared -PathType Leaf)) {
            $texts += Get-Item -LiteralPath $declared
        } else { throw "许可文件无效：$package" }
    }
    $paths = @(foreach ($file in ($texts | Sort-Object FullName -Unique)) {
        $relative = [IO.Path]::GetRelativePath($packageRoot, $file.FullName)
        $destination = Join-Path $target $relative
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
        [IO.Path]::GetRelativePath($Output, $destination).Replace('\', '/')
    })
    $matched = @($sources | Where-Object {
        ($repository -and $_.repositoryCommit -and $_.repositoryCommit -eq $repository.GetAttribute('commit')) -or $_.package -eq $name
    })
    foreach ($source in $matched) {
        Copy-Item -LiteralPath (Join-Path $upstreamRoot $source.file) -Destination (Join-Path $target $source.file)
        $paths += "$package/$($source.file)"
    }
    if (!$paths.Count) { throw "依赖缺少随包许可原文，请核对并补充：$package" }
    [ordered]@{
        package = $name; version = $version; type = $library.type
        license = if ($license) { $license.InnerText } else { $null }
        repository = if ($repository) { $repository.GetAttribute('url') } else { $null }
        repositoryCommit = if ($repository) { $repository.GetAttribute('commit') } else { $null }
        files = @($paths | Sort-Object -Unique)
        additionalSources = @($matched | ForEach-Object { $_.source })
    }
})
ConvertTo-Json -InputObject $records -Depth 6 | Set-Content -LiteralPath (Join-Path $Output 'dependencies.json') -Encoding utf8
