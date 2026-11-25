param(
  [string]$OutputPath = (Join-Path $PSScriptRoot '..\TaskbarDot.exe')
)

# Rebuild TaskbarDot.exe with the .NET Framework compiler that ships with
# Windows 10/11. No SDK, no NuGet, no signing key.
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
  $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
  throw 'csc.exe not found (.NET Framework 4.x is required)'
}

$source = Join-Path $PSScriptRoot '..\TaskbarDot.cs'
& $csc /nologo /target:winexe /optimize+ /out:$OutputPath /r:System.Drawing.dll $source
if ($LASTEXITCODE -ne 0) {
  throw "csc failed with exit code $LASTEXITCODE"
}
Get-Item $OutputPath | Select-Object FullName, Length, LastWriteTime
