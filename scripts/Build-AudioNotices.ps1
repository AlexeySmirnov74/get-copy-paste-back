[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZipPath = Join-Path $taskWorkspace 'build\Audio-Licenses.zip'
if (Test-Path -LiteralPath $taskZipPath) { Remove-Item -LiteralPath $taskZipPath }
$taskZip = [IO.Compression.ZipFile]::Open($taskZipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskVendor in @('aicodix','quiet')) {
        $taskVendorRoot = Join-Path $taskWorkspace ('third_party\'+$taskVendor)
        foreach ($taskFile in Get-ChildItem -LiteralPath $taskVendorRoot -Recurse -File) {
            if ($taskFile.Name -notmatch '^(LICENSE|COPYING|COPYRIGHT|NOTICE|lesser|GPL|LGPL)([._-].*)?$|^(README\.txt|PROVENANCE\.json|SOURCES\.json)$' -and $taskFile.FullName -notlike '*\toolchain-licenses\*') { continue }
            $taskRelative = $taskVendor+'/'+$taskFile.FullName.Substring($taskVendorRoot.Length+1).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, $taskFile.FullName, $taskRelative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
} finally { $taskZip.Dispose() }
Write-Output ('Audio licenses bundled: '+$taskZipPath)
