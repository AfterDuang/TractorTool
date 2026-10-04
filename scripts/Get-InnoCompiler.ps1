#requires -Version 7.4
# Build-time tool only. Portable mode creates no uninstaller or file associations.
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (Test-Path -LiteralPath $destination) { throw '编译器目录必须是尚不存在的新目录。' }
for ($ancestor = Split-Path $destination; $ancestor; $ancestor = Split-Path $ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '编译工具目录不能经过目录链接。' }
}
$version = '6.7.3'
$hash = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
New-Item -ItemType Directory -Path $destination | Out-Null
$download = Join-Path $destination "innosetup-$version.exe"
Invoke-WebRequest "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-$version.exe" -OutFile $download
if ((Get-FileHash -LiteralPath $download).Hash -ne $hash) { throw 'Inno Setup 编译工具 SHA256 不匹配。' }
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.') {
    throw 'Inno Setup 编译工具签名无效。'
}
$compiler = Join-Path $destination 'compiler'
$process = Start-Process -FilePath $download -ArgumentList @('/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', "/DIR=`"$compiler`"") -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $compiler 'ISCC.exe'))) { throw '准备便携编译工具失败。' }
Write-Output (Join-Path $compiler 'ISCC.exe')
