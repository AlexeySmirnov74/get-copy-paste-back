[CmdletBinding()]
param([int]$Seconds = 6, [switch]$OptimizedSubset)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path $env:LOCALAPPDATA 'HorizonServer'
$server = Join-Path $project 'build\Server.exe'
$bench = Join-Path $project 'build\CameraThroughputBench.exe'
$results = Join-Path $project $(if ($OptimizedSubset) { 'build\local-video-matrix-optimized' } else { 'build\local-video-matrix' })
[IO.Directory]::CreateDirectory($results) | Out-Null
$configPath = Join-Path $root 'data\camera.local.json'
$identity = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$serial = [string]$identity.camera.serial
$name = [string]$identity.camera.name
if (-not $serial.StartsWith('HorizonClient-')) { throw 'The active camera is not owned by this application.' }
$key = Join-Path $root 'data\camera.local.key'
function Set-TestMode([int]$width, [int]$height, [int]$fps, [int]$raw) {
    $arguments = '--configure-server "' + $root + '" ' + $width + ' ' + $height + ' ' + $fps + ' ' + $raw + ' 0'
    $helper = Start-Process -FilePath $server -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    if (-not $helper.WaitForExit(120000)) { throw 'Camera configuration did not complete.' }
    $result = Get-Content -LiteralPath (Join-Path $root 'data\configure-result.json') -Raw | ConvertFrom-Json
    if ($helper.ExitCode -ne 0 -or -not $result.Success) { throw ($result | ConvertTo-Json -Compress) }
    $current = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if ($current.camera.serial -ne $serial -or $current.camera.name -ne $name) { throw 'Camera identity changed unexpectedly.' }
}
$cases = @(
    @{ W=1280; H=720; F=30; R=0; P='Dense'; Q=90 },
    @{ W=1280; H=720; F=30; R=0; P='Dense'; Q=100 },
    @{ W=1280; H=720; F=30; R=0; P='HighDensity720'; Q=90 },
    @{ W=1280; H=720; F=30; R=0; P='HighDensity720'; Q=100 },
    @{ W=1280; H=720; F=30; R=0; P='Mp4Modem'; Q=90 },
    @{ W=1920; H=1080; F=30; R=0; P='HighDefinitionRobust'; Q=90 },
    @{ W=1920; H=1080; F=30; R=0; P='HighDensity1080'; Q=90 },
    @{ W=3840; H=2160; F=30; R=0; P='Ultra4KRobust'; Q=90 },
    @{ W=3840; H=2160; F=30; R=0; P='Maximum4K'; Q=90 },
    @{ W=3840; H=2160; F=30; R=0; P='Maximum4K'; Q=100 },
    @{ W=3840; H=2160; F=30; R=1; P='Maximum4K'; Q=90 },
    @{ W=7680; H=4320; F=30; R=0; P='Maximum4K'; Q=90 },
    @{ W=7680; H=4320; F=8; R=1; P='Maximum4K'; Q=90 }
)
$mode = ''
if ($OptimizedSubset) { $cases = @($cases | Where-Object { $_.P -in @('HighDensity720', 'HighDensity1080', 'Maximum4K') }) }
try {
    foreach ($case in $cases) {
        $next = '{0}x{1}-{2}-{3}' -f $case.W,$case.H,$case.F,$case.R
        if ($next -ne $mode) { Set-TestMode $case.W $case.H $case.F $case.R; $mode = $next }
        $devices = & (Join-Path $project 'build\SeiProbeSmoke.exe') --list | ForEach-Object { $_ | ConvertFrom-Json }
        $own = @($devices | Where-Object { $_.name -eq $name -and $_.id -like '*vid_1209*pid_f002*' })
        if ($own.Count -ne 1) { throw 'Expected exactly one selected application camera.' }
        $label = '{0}-{1}-q{2}' -f $mode,$case.P,$case.Q
        Write-Output ('CASE ' + $label)
        & $bench --key $key --camera $own[0].id --profile $case.P --quality $case.Q --seconds $Seconds --output (Join-Path $results ($label + '.json'))
        Write-Output ('EXIT ' + $LASTEXITCODE)
    }
}
finally { Set-TestMode 1280 720 30 0; Write-Output ('Restored 1280x720 MJPEG30, same camera: ' + $name + ' / ' + $serial) }
