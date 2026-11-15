# Build the embedded WebView2 camera inspection bridge. This helper is local-only.
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$pin=Get-Content -LiteralPath (Join-Path $root 'webview2-sdk.json') -Raw|ConvertFrom-Json
if($pin.schema-ne 1-or $pin.package-cne 'Microsoft.Web.WebView2'-or $pin.version-notmatch '^1\.0\.[0-9]+\.[0-9]+$'-or $pin.architecture-cne 'x64'-or $pin.cache_directory-cne ('tools/webview2-sdk/'+$pin.version)){throw 'Invalid reviewed WebView2 SDK pins.'}
$cache=Join-Path $root $pin.cache_directory
$expected=@('include/WebView2.h','include/WebView2EnvironmentOptions.h','x64/WebView2LoaderStatic.lib','LICENSE.txt','NOTICE.txt')
if(@($pin.files).Count-ne $expected.Count){throw 'Unexpected WebView2 SDK file count.'}
for($index=0;$index-lt $expected.Count;$index++){
    $entry=$pin.files[$index];$path=Join-Path $cache $expected[$index]
    if($entry.file-cne $expected[$index]-or $entry.sha256-notmatch '^[A-Fa-f0-9]{64}$'){throw 'Unexpected WebView2 SDK file pin.'}
    if(-not(Test-Path -LiteralPath $path)){throw ('Missing pinned WebView2 SDK input. Run Download-WebViewSdk.ps1 once at build time: '+$entry.file)}
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash-ne $entry.sha256){throw ('Pinned WebView2 SDK member hash mismatch: '+$entry.file)}
}
if(-not(Get-Command cl.exe -ErrorAction SilentlyContinue)){
    $vcvars=Join-Path $root 'tools\buildtools\VC\Auxiliary\Build\vcvars64.bat'
    if(-not(Test-Path -LiteralPath $vcvars)){
        $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if(-not(Test-Path -LiteralPath $vswhere)){throw 'MSVC C++ Build Tools and Windows SDK are required.'}
        $installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if(-not $installation){throw 'MSVC C++ Build Tools were not found.'}
        $vcvars=Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
    }
    $environment=& cmd.exe /d /c ('"'+$vcvars+'" >nul && set')
    if($LASTEXITCODE-ne 0){throw 'Cannot initialize C++ compiler environment.'}
    foreach($line in $environment){if($line-match '^(PATH|INCLUDE|LIB|LIBPATH)=(.*)$'){[Environment]::SetEnvironmentVariable($Matches[1],$Matches[2],'Process')}}
}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$object=Join-Path $OutputDirectory 'webview_camera_native.obj'
& cl.exe /nologo /c /EHsc /O2 /MT /std:c++20 /utf-8 /W4 /WX /DWIN32_LEAN_AND_MEAN /DNOMINMAX /DWEBVIEW2_USE_STATIC_LOADER ('/external:I'+(Join-Path $cache 'include')) /external:W0 (Join-Path $root 'src\webview_camera_native.cpp') ('/Fo:'+$object)
if($LASTEXITCODE-ne 0){throw 'WebView2 camera inspection bridge compilation failed.'}
& lib.exe /nologo ('/OUT:'+(Join-Path $OutputDirectory 'usbbridge_webview_camera.lib')) $object
if($LASTEXITCODE-ne 0){throw 'WebView2 camera inspection bridge archive failed.'}

Write-Output ('cargo:rustc-link-search=native='+(Join-Path $cache 'x64'))
