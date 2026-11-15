[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$checks = 0
function Assert-Mode {
    param($Actual, [int]$Width, [int]$Height, [int]$FPS, [bool]$Raw, [bool]$Probe, [string]$Reason)
    $script:checks++
    if ($Actual.Width -ne $Width -or $Actual.Height -ne $Height -or $Actual.FPS -ne $FPS -or $Actual.Uncompressed -ne $Raw -or $Actual.H264Probe -ne $Probe) {
        throw $Reason
    }
}
# Extract only pure selectors. Never execute the setup script body, enumerate
# hardware, invoke a receiver, request UAC or access real runtime metadata/keys.
foreach ($entry in @(
    @{ Path = 'scripts\Camera-Runtime.ps1'; Name = 'Test-HorizonVideoMode' },
    @{ Path = 'scripts\Setup-Camera.ps1'; Name = 'Test-HorizonSetupVideoMode' },
    @{ Path = 'scripts\Setup-Camera.ps1'; Name = 'Get-HorizonSetupCompatibleMode' },
    @{ Path = 'scripts\Setup-Camera.ps1'; Name = 'Get-HorizonSetupVideoMode' }
)) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $projectDirectory $entry.Path), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw ('Invalid PowerShell source: ' + $entry.Path) }
    $name = $entry.Name
    $function = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
    if ($function.Count -ne 1) { throw ('Missing pure selector: ' + $name) }
    . ([ScriptBlock]::Create($function[0].Extent.Text))
}
$defaults = Get-Content -LiteralPath (Join-Path $projectDirectory 'defaults\ServerDefaults.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($defaults.width -ne 1920 -or $defaults.height -ne 1080 -or $defaults.fps -ne 30 -or $defaults.uncompressed -ne $true -or $defaults.h264_probe -ne $false) { throw 'Bundled defaults must select1080p30 YUY2 without a probe.' }
$checks++
$existing = [pscustomobject]@{ Width = 640; Height = 480; FPS = 15; Uncompressed = $true; H264Probe = $false; CameraSerial = 'fixture-preserved-identity'; CameraName = 'Fixture original' }
$before = [Management.Automation.PSSerializer]::Serialize($existing)
Assert-Mode (Get-HorizonSetupVideoMode -Defaults $defaults -Parameters @{}) 1920 1080 30 $true $false 'New cameras without explicit settings select1080p30 YUY2.'
$selected = @{ Width = 1920; Height = 1080; FramesPerSecond = 30; Uncompressed = $true; H264Probe = $false }
Assert-Mode (Get-HorizonSetupVideoMode -Defaults $defaults -Parameters $selected) 1920 1080 30 $true $false 'New1080YUY2 camera setup must honor the GUI selection directly.'
Assert-Mode (Get-HorizonSetupVideoMode -Defaults $defaults -Parameters @{} -Existing $existing) 640 480 15 $true $false 'Legacy setup and upgrades preserve the saved mode.'
Assert-Mode (Get-HorizonSetupVideoMode -Defaults $defaults -Parameters $selected -Existing $existing) 1920 1080 30 $true $false 'Explicit selected mode must not be replaced by saved metadata.'
$selected.Uncompressed = $false
Assert-Mode (Get-HorizonSetupVideoMode -Defaults $defaults -Parameters $selected -Existing $existing) 1920 1080 30 $false $false 'The selector preserves an explicitly false raw switch for validation.'
if (Test-HorizonSetupVideoMode 1920 1080 30 $false) { throw 'Explicit compressed configuration must fail validation.' }; $checks++
$probe = @{ Width = 640; Height = 480; FramesPerSecond = 30; Uncompressed = $false; H264Probe = $true }
Assert-Mode (Get-HorizonSetupVideoMode -Defaults $defaults -Parameters $probe -Existing $existing) 640 480 30 $false $true 'The selector preserves an explicitly selected SEI mode for validation.'
if (Test-HorizonSetupVideoMode 640 480 30 $false $true) { throw 'Explicit SEI probe configuration must fail validation.' }; $checks++
$legacy = [pscustomobject]@{ Width = 3840; Height = 2160; FPS = 30; Uncompressed = $true; H264Probe = $false }
Assert-Mode (Get-HorizonSetupVideoMode -Defaults $defaults -Parameters @{} -Existing $legacy) 1920 1080 30 $true $false 'Old4K YUY2 setup normalizes to1080p without creating another identity.'
foreach ($dimensions in @(@(2560,1440),@(3840,2160))) {
    $w=$dimensions[0]; $h=$dimensions[1]
    $manual=@{ Width=$w; Height=$h; FramesPerSecond=30; Uncompressed=$true; H264Probe=$false }
    Assert-Mode (Get-HorizonSetupVideoMode -Defaults $defaults -Parameters $manual) $w $h 30 $true $false 'The selector preserves an unsupported explicit geometry for fail-closed validation.'
    if ((Test-HorizonSetupVideoMode $w $h 30 $true) -or (Test-HorizonSetupVideoMode $w $h 60 $true) -or (Test-HorizonSetupVideoMode $w $h 30 $false)) { throw 'High-resolution configuration must always fail validation.' }
    $checks++
    Assert-Mode (Get-HorizonSetupVideoMode -Defaults $defaults -Parameters @{} -Existing ([pscustomobject]@{Width=$w;Height=$h;FPS=30;Uncompressed=$true;H264Probe=$false})) 1920 1080 30 $true $false 'Legacy high-resolution metadata selects1080p YUY2 for migration.'
}
foreach ($dimensions in @(@(7680,4320),@(2560,1600),@(1920,1082),@(800,600),@(640,720),@(1920,720))) {
    if (Test-HorizonSetupVideoMode $dimensions[0] $dimensions[1] 30 $true) { throw 'Unsupported high-resolution geometry was accepted.' }
    $checks++
}
foreach ($dimensions in @(@(640,480),@(1280,720),@(1920,1080))) {
    foreach ($fps in @(1,15,30,60)) {
        if (-not (Test-HorizonSetupVideoMode $dimensions[0] $dimensions[1] $fps $true)) { throw 'The three supported YUY2 geometries must preserve bounded frame rates.' }; $checks++
    }
    if ((Test-HorizonSetupVideoMode $dimensions[0] $dimensions[1] 30 $false) -or (Test-HorizonSetupVideoMode $dimensions[0] $dimensions[1] 30 $true $true)) { throw 'Supported geometry cannot enable compression or SEI.' }; $checks++
}
$raw720 = [pscustomobject]@{ Width = 1280; Height = 720; FPS = 30; Uncompressed = $true; H264Probe = $false }
Assert-Mode (Get-HorizonSetupVideoMode -Defaults $defaults -Parameters @{} -Existing $raw720) 1280 720 30 $true $false 'An existing720p YUY2 camera remains unchanged without an explicit mode.'
foreach ($compressed in @(
    [pscustomobject]@{ Width = 640; Height = 480; FPS = 15; Uncompressed = $false; H264Probe = $false },
    [pscustomobject]@{ Width = 1280; Height = 720; FPS = 60; Uncompressed = $false; H264Probe = $false },
    [pscustomobject]@{ Width = 640; Height = 480; FPS = 30; Uncompressed = $false; H264Probe = $true }
)) {
    $saved = [Management.Automation.PSSerializer]::Serialize($compressed)
    Assert-Mode (Get-HorizonSetupVideoMode -Defaults $defaults -Parameters @{} -Existing $compressed) 1920 1080 30 $true $false 'Legacy compressed/probe setup without explicit settings migrates to1080p30 YUY2.'
    if ([Management.Automation.PSSerializer]::Serialize($compressed) -cne $saved) { throw 'Migration selection modified existing camera metadata.' }
}
# Exercise actual PSBoundParametersDictionary and SwitchParameter values too.
function Invoke-ModeBindingFixture {
    [CmdletBinding()]
    param([int]$Width, [int]$Height, [int]$FramesPerSecond, [switch]$Uncompressed, [switch]$H264Probe)
    Get-HorizonSetupVideoMode -Defaults $defaults -Parameters $PSBoundParameters -Existing $existing
}
Assert-Mode (Invoke-ModeBindingFixture) 640 480 15 $true $false 'An empty actual bound-parameter dictionary retains the existing mode.'
Assert-Mode (Invoke-ModeBindingFixture -Width 1920 -Height 1080 -FramesPerSecond 30 -Uncompressed:$false -H264Probe:$false) 1920 1080 30 $false $false 'PowerShell false SwitchParameter values remain explicit selections.'
if ([Management.Automation.PSSerializer]::Serialize($existing) -cne $before) { throw 'Mode selection changed the saved identity fixture.' }
$checks++
Write-Output ('Setup selected-mode checks passed: {0}. Pure selectors only; no cameras, drivers, runtime files or key access.' -f $checks)
