[CmdletBinding()]
param([string[]]$Path)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Path) {
    $Path = @(Get-ChildItem -LiteralPath (Join-Path $project 'scripts'),(Join-Path $project 'tests') -Filter *.ps1 -File -Recurse | ForEach-Object { $_.FullName })
}
$decoder = New-Object Text.UTF8Encoding($false,$true)
foreach ($file in $Path) {
    $bytes = [IO.File]::ReadAllBytes($file)
    try { $text = $decoder.GetString($bytes) }
    catch { throw ('PowerShell source is not valid UTF-8: '+[IO.Path]::GetFileName($file)) }
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191
    if ($text -match '[^\x00-\x7F]' -and !$hasBom) {
        throw ('PowerShell 5.1 source with non-ASCII text requires a UTF-8 BOM: '+[IO.Path]::GetFileName($file))
    }
}
Write-Output ('Script encoding checks passed: '+$Path.Count+' UTF-8 source files; non-ASCII PowerShell requires BOM. Source bytes only, no scripts executed.')
