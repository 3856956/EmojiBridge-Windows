$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4 compiler not found.' }
$output = Join-Path $root 'EmojiBridge.exe'
$sources = @('EmojiBridge.cs', 'WeChatExtractor.cs', 'QqStickerSources.cs', 'StickerPicker.cs', 'SelfTest.cs') | ForEach-Object { Join-Path $root "src/$_" }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$output" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $output"
