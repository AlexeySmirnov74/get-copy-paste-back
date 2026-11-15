# Pure build-input fingerprinting: no application/device/settings operations.
function Get-HorizonBuildInputFingerprint {
    param([Parameter(Mandatory=$true)][string]$ProjectDirectory)
    $project = [IO.Path]::GetFullPath($ProjectDirectory)
    $paths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($directory in @('src','tests','scripts','defaults','assets','third_party','runtime\licenses','runtime\audio')) {
        $base = Join-Path $project $directory
        if (!(Test-Path -LiteralPath $base -PathType Container)) { throw ('Missing build input directory: '+$directory) }
        foreach ($file in Get-ChildItem -LiteralPath $base -File -Recurse) {
            $paths.Add($file.FullName.Substring($project.Length+1).Replace('\','/'))
        }
    }
    foreach ($name in @('runtime/usbbridge-camera.next.exe','runtime/drivers/USBip-0.9.8.1-x64.exe','runtime/USBBridge-LICENSE.txt','runtime/THIRD-PARTY.txt','runtime/PROVENANCE.txt')) { $paths.Add($name) }
    $paths.Sort([StringComparer]::Ordinal)
    $records = New-Object 'System.Collections.Generic.List[string]'
    foreach ($relative in $paths) {
        $stream = [IO.File]::OpenRead((Join-Path $project $relative))
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-','') }
        finally { $hasher.Dispose(); $stream.Dispose() }
        $records.Add($relative+"`0"+$hash)
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes([String]::Join("`n", $records.ToArray()))
    $aggregate = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($aggregate.ComputeHash($bytes)).Replace('-','') }
    finally { $aggregate.Dispose() }
    [pscustomobject]@{ SHA256=$digest; FileCount=$paths.Count }
}
