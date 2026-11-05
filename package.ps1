$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = $PSScriptRoot
$files = @('EmojiBridge.exe', 'README.md', 'THIRD-PARTY-NOTICES.txt', 'third_party/wxemoticon/wxemoticon.exe')
foreach ($entry in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $root $entry) -PathType Leaf)) { throw "Missing package input: $entry" }
}
$out = Join-Path $root 'EmojiBridge-Windows.zip'
$pending = Join-Path $root ('EmojiBridge-package-' + [Guid]::NewGuid().ToString('N') + '.zip')
$archive = [System.IO.Compression.ZipFile]::Open($pending, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $files) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root $entry), $entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }
Move-Item -LiteralPath $pending -Destination $out -Force
Write-Output "Packaged: $out"
