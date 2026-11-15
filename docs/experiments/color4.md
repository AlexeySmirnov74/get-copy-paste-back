# Get Copy-Paste Back: four-color video modes

The original local experiment was successfully tested through a real Horizon
session and adopted as an optional server feature in 0.6.37. The client always
understands these frames; the server setting controls which modes it offers.
The current Get Copy-Paste Back programs are `Server.exe` on the local PC and
`Client.exe` inside the remote desktop; older experiment records retain their
historical application names.

In Settings, **Four-color modes** adds four choices to both the Work dropdown
and speed test. Turning it off restores the five ordinary modes. The preference
is saved in portable `server.settings.xml` as `colorModesEnabled`; it defaults
off. Old experiment settings with a selected color mode and no flag migrate to
enabled. Explicit false/malformed flags win. Camera source formats remain the
same three YUY2 choices; no driver update is involved.

| Wire ID | Mode | Encoded image | Cell | Payload/frame |
|---|---|---|---|---:|
| 12 | Color720 | 1280×720 | 8 px | 3072 B |
| 13 | Color720Dense | 1280×720 | 4 px | 12288 B |
| 14 | Color1080 | 1920×1080 | 8 px | 6144 B |
| 15 | Color1080Dense | 1920×1080 | 4 px | 24576 B |

Black/red/yellow/blue cells carry two bits each. Every frame includes four
wide reference bands. The receiver calibrates its nearest-color classifier,
checks metadata bounds and verifies a CRC over the header and payload before
returning a packet. Existing transfer assembly, compression, duplicate rejection
and delivery receipts remain in force. Lazy row reads keep failed color probes
from copying entire ordinary monochrome frames.

Centered crop and mirror recovery accommodate the existing camera geometry.
YUY2 shares horizontal chroma, so cell widths and boundaries are even. Horizon
may still compress, reduce or otherwise process the video. Synthetic tests cover
RGB, JPEG, YUY2, 4:2:0, offsets/gains, resize, mirroring, corrupted headers/data,
collapsed calibration colors and grayscale rejection. Dense modes can reject
degraded frames correctly; they are not guaranteed on every connection.

The reported real-session check used 1920×1080 capture and verified every byte
of 10 KiB and 100 KiB files in all nine modes. See the README for measured
single-session results and separate calculated ceilings. Logs, device identities
and user files are not part of the repository or release assets.

Disabling color while a color transfer is pending switches it to the ordinary
1080p robust mode under a fresh transfer ID, preserving its data and generation.
Mode changes are locked while a speed test or its cancellation tail is active.
Completed results remain visible for comparison. Switching physical camera
formats still requires reconnecting the whole Horizon remote desktop.
