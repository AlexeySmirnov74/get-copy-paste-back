$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $project 'scripts\Build-Inputs.ps1')
$build = [IO.Path]::GetFullPath((Join-Path $project 'build'))
$fixture = Join-Path $build ('build-inputs-fixtures-'+[Guid]::NewGuid().ToString('N'))
$checks = 0
function Check([bool]$Condition,[string]$Message) { if (!$Condition) { throw $Message }; $script:checks++ }
try {
    $roots = @((Join-Path $fixture 'first'),(Join-Path $fixture 'second'))
    foreach ($root in $roots) {
        foreach ($directory in @('src','tests','scripts','defaults','assets','third_party','runtime\licenses','runtime\audio')) {
            [IO.Directory]::CreateDirectory((Join-Path $root $directory)) | Out-Null
            [IO.File]::WriteAllBytes((Join-Path $root ($directory+'\fixture.txt')), [byte[]](1,2,3))
        }
        [IO.Directory]::CreateDirectory((Join-Path $root 'runtime\drivers')) | Out-Null
        foreach ($file in @('runtime/usbbridge-camera.next.exe','runtime/drivers/USBip-0.9.8.1-x64.exe','runtime/USBBridge-LICENSE.txt','runtime/THIRD-PARTY.txt','runtime/PROVENANCE.txt')) {
            [IO.File]::WriteAllBytes((Join-Path $root $file), [byte[]](4,5,6))
        }
    }
    $baseline = Get-HorizonBuildInputFingerprint -ProjectDirectory $roots[0]
    $copy = Get-HorizonBuildInputFingerprint -ProjectDirectory $roots[1]
    Check ($baseline.FileCount -eq 13) 'All fixture input classes must be hashed.'
    Check ($baseline.SHA256 -eq $copy.SHA256) 'Identical inputs under different roots must hash equally.'
    foreach ($relative in @('src/fixture.txt','tests/fixture.txt','scripts/fixture.txt','defaults/fixture.txt','assets/fixture.txt','third_party/fixture.txt','runtime/licenses/fixture.txt','runtime/audio/fixture.txt','runtime/usbbridge-camera.next.exe','runtime/drivers/USBip-0.9.8.1-x64.exe','runtime/USBBridge-LICENSE.txt','runtime/THIRD-PARTY.txt','runtime/PROVENANCE.txt')) {
        $path = Join-Path $roots[1] $relative
        $bytes = [IO.File]::ReadAllBytes($path); $stamp = [IO.File]::GetLastWriteTimeUtc($path)
        try {
            [IO.File]::WriteAllBytes($path, ($bytes+[byte]7)); [IO.File]::SetLastWriteTimeUtc($path,$stamp)
            $changed = Get-HorizonBuildInputFingerprint -ProjectDirectory $roots[1]
            Check ($changed.SHA256 -ne $baseline.SHA256) ('Content changes must invalidate validation even with preserved timestamps: '+$relative)
        } finally { [IO.File]::WriteAllBytes($path,$bytes); [IO.File]::SetLastWriteTimeUtc($path,$stamp) }
    }
    $extra = Join-Path $roots[1] 'src\added.cs'
    [IO.File]::WriteAllText($extra,'synthetic added input')
    $added = Get-HorizonBuildInputFingerprint -ProjectDirectory $roots[1]
    Check ($added.FileCount -eq 14 -and $added.SHA256 -ne $baseline.SHA256) 'Added inputs must invalidate validation.'
    Remove-Item -LiteralPath $extra
    $restored = Get-HorizonBuildInputFingerprint -ProjectDirectory $roots[1]
    Check ($restored.SHA256 -eq $baseline.SHA256) 'Restoring exact inputs must restore their fingerprint.'
    Write-Output ('Build-input checks passed: '+$checks+'. Content-bound validation, embedded inputs, preserved timestamps and relocated roots; private synthetic files only.')
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith($build+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^build-inputs-fixtures-[a-f0-9]{32}$') { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
