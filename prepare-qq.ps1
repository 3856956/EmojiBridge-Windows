param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'EmojiBridge.exe'))
$resolvedSource = (Resolve-Path -LiteralPath $Source).ProviderPath
$stickers = New-Object 'System.Collections.Generic.List[EmojiBridge.Sticker]'
$hashes = New-Object 'System.Collections.Generic.HashSet[string]'
$issues = New-Object 'System.Collections.Generic.List[string]'
$duplicates = 0
$images = @(Get-ChildItem -LiteralPath $resolvedSource -File | Sort-Object Name | Where-Object { [EmojiBridge.Converter]::Extensions.Contains($_.Extension) })
foreach ($file in $images) {
    try {
        $image = [EmojiBridge.Converter]::Inspect($file.FullName)
        if ($hashes.Add($image.Hash)) { $stickers.Add($image) } else { $duplicates++ }
    } catch { $issues.Add($file.Name + ': ' + $_.Exception.Message) }
}
if ($stickers.Count -eq 0) { throw 'No supported images were found.' }
$options = New-Object EmojiBridge.ExportOptions
$options.Destination = $Destination
$result = [EmojiBridge.Converter]::Export($stickers, $options, [Action[int,string]]{ param($count,$name) })
$gifCount = @($stickers | Where-Object Format -eq 'GIF').Count
$animationCount = @($stickers | Where-Object Frames -gt 1).Count
$exportedHashes = New-Object 'System.Collections.Generic.HashSet[string]'
Get-ChildItem -LiteralPath $result.Directory -Filter '*.gif' -File | ForEach-Object { [void]$exportedHashes.Add([EmojiBridge.Converter]::HashFile($_.FullName)) }
$unmatchedGifs = @($stickers | Where-Object { $_.Format -eq 'GIF' -and !$exportedHashes.Contains($_.Hash) }).Count
[PSCustomObject]@{
    extracted = $images.Count
    exported = $result.Count
    duplicate = $duplicates
    rejected = $issues.Count
    failed = $result.Errors.Count
    gif = $gifCount
    animatedGif = $animationCount
    unmatchedGifs = $unmatchedGifs
    directory = $result.Directory
    zip = $result.ZipPath
} | ConvertTo-Json -Compress
if ($unmatchedGifs -gt 0 -or $issues.Count -gt 0 -or $result.Errors.Count -gt 0) { exit 1 }
