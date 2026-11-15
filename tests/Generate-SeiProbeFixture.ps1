[CmdletBinding()]
param([string]$FFmpeg = 'C:\Projects\cam\ffmpeg.exe')
$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot '..\native\usbbridge\assets\horizon-sei-probe.h264'
& $FFmpeg -hide_banner -loglevel error -f lavfi -i 'color=c=black:s=640x480:r=30' -frames:v 1 -c:v libx264 -profile:v baseline -level:v 3.0 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -x264-params 'keyint=1:scenecut=0:repeat-headers=1:slices=1' -bsf:v 'h264_metadata=sei_user_data=6b82d43f-4d4f-4a2a-9967-0d620761c001+HORIZON_SEI_PROBE_V1' -f h264 -y $target
if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture generation failed.' }
Get-FileHash -LiteralPath $target -Algorithm SHA256
