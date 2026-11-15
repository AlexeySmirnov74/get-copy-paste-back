[CmdletBinding()]
param([string]$Ffmpeg = 'C:\Projects\cam\ffmpeg.exe')
$ErrorActionPreference = 'Stop'
$outputDirectory = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) 'build\video-metadata-bench'
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$marker = 'HORIZON_SYNTHETIC_SEI_PAYLOAD_6B82D43F'
$source = Join-Path $outputDirectory 'source.h264'
$copy = Join-Path $outputDirectory 'copy.h264'
$transcoded = Join-Path $outputDirectory 'transcoded.h264'
$pixels = Join-Path $outputDirectory 'pixels.rgb'
$fromPixels = Join-Path $outputDirectory 'from-pixels.h264'
function Invoke-Ffmpeg([string[]]$Arguments) {
    & $Ffmpeg -hide_banner -loglevel error -y @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('FFmpeg failed with code ' + $LASTEXITCODE) }
}
Invoke-Ffmpeg @('-f','lavfi','-i','color=c=black:s=320x240:r=5','-t','1','-c:v','libx264','-bsf:v',('h264_metadata=sei_user_data=ad458634-fd95-455c-a2d1-0dced45c27aa+' + $marker),'-an','-f','h264',$source)
Invoke-Ffmpeg @('-i',$source,'-c:v','copy','-an','-f','h264',$copy)
Invoke-Ffmpeg @('-i',$source,'-c:v','libx264','-an','-f','h264',$transcoded)
Invoke-Ffmpeg @('-i',$source,'-pix_fmt','rgb24','-an','-f','rawvideo',$pixels)
Invoke-Ffmpeg @('-f','rawvideo','-pixel_format','rgb24','-video_size','320x240','-framerate','5','-i',$pixels,'-c:v','libx264','-an','-f','h264',$fromPixels)
$result = [ordered]@{ Test='Synthetic FFmpeg only; not a Horizon capture'; Marker=$marker }
foreach ($entry in @(@{Name='OriginalContainsMarker';Path=$source},@{Name='BitstreamCopyContainsMarker';Path=$copy},@{Name='DirectTranscodeContainsMarker';Path=$transcoded},@{Name='DecodedPixelsContainMarker';Path=$pixels},@{Name='PixelsReencodedContainMarker';Path=$fromPixels})) {
    $result[$entry.Name] = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($entry.Path)).Contains($marker)
}
$json = $result | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $outputDirectory 'result.json'), $json, [Text.UTF8Encoding]::new($false))
$json
