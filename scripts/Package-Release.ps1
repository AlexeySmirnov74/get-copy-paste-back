[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$ValidationLog,
    [switch]$UpdateRoot
)
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildDirectory = Join-Path $projectDirectory 'build'
$source = [IO.File]::ReadAllText((Join-Path $projectDirectory 'src\AssemblyInfo.cs'))
$sourceVersions = @([regex]::Matches($source, 'AssemblyFileVersion\("(\d+\.\d+\.\d+)\.0"\)') | ForEach-Object { $_.Groups[1].Value })
if ($sourceVersions.Count -ne 2 -or $sourceVersions[0] -ne $sourceVersions[1]) { throw 'Server/client source versions must agree.' }
if (!$Version) { $Version = $sourceVersions[0] }
if ($Version -ne $sourceVersions[0]) { throw 'Requested release version does not match both applications.' }
[IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
if (!$ValidationLog) {
    $ValidationLog = Join-Path $buildDirectory ('validation-'+$Version+'.txt')
    $previousErrorAction = $ErrorActionPreference
    try {
        # Capture the complete child error stream before deciding from its exit
        # code; Stop would abort log collection at its first stderr record.
        $ErrorActionPreference = 'Continue'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build.ps1') -NoPublish *> $ValidationLog
        $buildExitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousErrorAction }
    if ($buildExitCode -ne 0) { throw ('Full build/tests failed. See '+$ValidationLog) }
}
$ValidationLog = [IO.Path]::GetFullPath($ValidationLog)
$validation = Get-Item -LiteralPath $ValidationLog
if (!(Select-String -LiteralPath $ValidationLog -SimpleMatch 'Verified tests: all configured suites passed.' -Quiet) -or
    !(Select-String -LiteralPath $ValidationLog -SimpleMatch 'Built without publishing:' -Quiet)) { throw 'A completed full build with tests is required; -SkipTests output is not accepted.' }
. (Join-Path $PSScriptRoot 'Build-Inputs.ps1')
$inputs = Get-HorizonBuildInputFingerprint -ProjectDirectory $projectDirectory
if (!(Select-String -LiteralPath $ValidationLog -SimpleMatch ('Verified build input SHA256: '+$inputs.SHA256) -Quiet)) { throw 'Validation does not match current source, tests, scripts and embedded inputs.' }
$names = @('Server.exe','Client.exe')
foreach ($name in $names) {
    $binary = Get-Item -LiteralPath (Join-Path $buildDirectory $name)
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($binary.FullName).FileVersion -ne ($Version+'.0')) { throw ('Unexpected binary version: '+$name) }
    $binaryHash = (Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash
    if (!(Select-String -LiteralPath $ValidationLog -SimpleMatch ('Verified binary SHA256 '+$name+': '+$binaryHash) -Quiet)) { throw ('Binary does not match validated output: '+$name) }
}
$distDirectory = Join-Path $projectDirectory 'dist'
$releaseDirectory = Join-Path $distDirectory ('Get-Copy-Paste-Back-'+$Version)
[IO.Directory]::CreateDirectory($releaseDirectory) | Out-Null
foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $buildDirectory $name) -Destination (Join-Path $releaseDirectory $name) -Force }
$startHere = @'
Get Copy-Paste Back / Windows x64

ENGLISH
Server.exe: run on your LOCAL PC. In Settings, create/start the camera.
In Horizon, select that camera and reconnect the remote desktop after changing
the camera format. Run Client.exe INSIDE the remote Windows desktop.
Use its tray menu to select the camera and the Horizon audio playback output.
The client has no main window. Copy NEW content with Ctrl+C. Wait for the high
confirmation sound on the local PC, then paste with Ctrl+V at the destination.
Reverse copies travel as audible modem data through Horizon playback. Avoid
music/system sounds during transfers. Speed and available camera modes depend
on your actual Horizon connection. Driver installation may require elevation.
Server settings/logs: HorizonServer\data beside its EXE. The client saves
settings under LocalAppData, then RoamingAppData, then its own folder; debug
logs remain beside its EXE. Do not send personal logs or received files publicly.

RUSSIAN / РУССКИЙ
Server.exe запускается на ЛОКАЛЬНОМ ПК. В Settings создайте камеру.
Выберите её в Horizon. После смены формата камеры закройте удалённый рабочий
стол и подключитесь снова. Client.exe запустите ВНУТРИ удалённой Windows.
В меню его значка в трее выберите камеру и аудиовыход Horizon. Главного окна
у клиента нет. Скопируйте НОВЫЕ данные через Ctrl+C. Дождитесь высокого
подтверждающего звука на локальном ПК и вставьте данные через Ctrl+V.
Обратная передача использует звук. Во время неё выключите музыку и системные
звуки. Скорость и доступные форматы зависят от подключения Horizon.
Язык можно изменить в настройках. Для установки драйвера нужны права администратора.

Docs / Документация:
https://github.com/AlexeySmirnov74/get-copy-paste-back
This project is independent of VMware/Omnissa. Third-party materials are embedded
in each EXE and can be exported with --export-third-party <destination>.
'@
[IO.File]::WriteAllText((Join-Path $releaseDirectory 'START-HERE.txt'), $startHere, (New-Object Text.UTF8Encoding($true)))
$packageNames = $names + @('START-HERE.txt')
$archivePath = Join-Path $distDirectory ('Get-Copy-Paste-Back-'+$Version+'.zip')
Compress-Archive -LiteralPath @($packageNames | ForEach-Object { Join-Path $releaseDirectory $_ }) -DestinationPath $archivePath -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    if ($archive.Entries.Count -ne $packageNames.Count) { throw 'Unexpected ZIP file count.' }
    foreach ($entry in $archive.Entries) {
        if ($packageNames -notcontains $entry.FullName) { throw ('Unexpected ZIP member: '+$entry.FullName) }
        $stream = $entry.Open(); $hasher = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-','') }
        finally { $hasher.Dispose(); $stream.Dispose() }
        if ($entryHash -ne (Get-FileHash -LiteralPath (Join-Path $releaseDirectory $entry.FullName) -Algorithm SHA256).Hash) { throw ('ZIP checksum mismatch: '+$entry.FullName) }
    }
} finally { $archive.Dispose() }
$assetPaths = @($names | ForEach-Object { Join-Path $releaseDirectory $_ }) + @($archivePath)
$checksums = @($assetPaths | ForEach-Object { ((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())+'  '+[IO.Path]::GetFileName($_) })
$checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
[IO.File]::WriteAllLines($checksumPath, $checksums, (New-Object Text.UTF8Encoding($false)))
if ($UpdateRoot) {
    $live = @(Get-Process -Name Server,Client,HorizonServer,HorizonClient -ErrorAction SilentlyContinue)
    if ($live.Count -eq 0) { foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $releaseDirectory $name) -Destination (Join-Path $distDirectory $name) -Force } }
    else { Write-Warning 'Active applications found; root dist EXEs were preserved. Use the new versioned folder.' }
}
$manifest = [pscustomobject]@{ Version=$Version; Assets=@($assetPaths)+@($checksumPath); Validation=$ValidationLog; Files=@($assetPaths | ForEach-Object { [pscustomobject]@{Name=[IO.Path]::GetFileName($_);Bytes=(Get-Item -LiteralPath $_).Length;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} }) }
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $buildDirectory ('release-'+$Version+'.json')) -Encoding UTF8
$manifest.Files | Format-Table -AutoSize
Write-Output ('Release package verified: '+$archivePath)
