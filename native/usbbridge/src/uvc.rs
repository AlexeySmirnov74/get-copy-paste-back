//! A software UVC 1.1 MJPEG, YUY2 or fixed H.264 test device over USB/IP.
//! Protocol layouts follow USB Video Class; no physical device is claimed.
use crate::{
    backend::{Provider, Session},
    wire::{self, Completion, Device, Submit},
};
use serde::{Deserialize, Serialize};
use std::{
    collections::VecDeque,
    io,
    sync::{
        Arc, Condvar, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{SyncSender, TrySendError},
    },
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

pub const BUSID: &str = "camera-1";
/// This software endpoint must never be recaptured by the physical USB backend.
pub fn is_emulated_identity(identity: &str) -> bool {
    let value = identity.to_ascii_uppercase();
    value.starts_with("USB\\VID_1209&PID_F002\\") || value.starts_with("USB\\VID_1209&PID_F002&MI_")
}

pub const MAX_JPEG: usize = 64 * 1024 * 1024;
pub const MAX_RAW_FRAME: usize = 64 * 1024 * 1024;
/// Synthetic Annex B fixture only; this is not a live H.264 encoder.
const H264_PROBE_FRAME: &[u8] = include_bytes!("../assets/horizon-sei-probe.h264");
/// Authenticated PCM ingress: 48 kHz, mono, signed little-endian 16-bit samples.
pub const MAX_AUDIO_CHUNK: usize = 192_000;
pub const MAX_AUDIO_QUEUE: usize = 960_000;
const PAYLOAD: usize = 16 * 1024;
const CLOCK: u32 = 48_000_000;
const AUDIO_RATE: u32 = 48_000;
const AUDIO_PACKET: usize = 96;
const AUDIO_MAX_PACKETS: usize = 1024;
const AUDIO_QUEUE: usize = 128;
fn audit_uvc(mut fields: serde_json::Value) {
    use std::io::Write as _;
    fields["unix_ms"] = serde_json::json!(
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis()
            .min(u128::from(u64::MAX)) as u64
    );
    // Bounded metadata only. Never render video/audio buffers, keys or identity
    // strings into the host application's stderr diagnostics transport.
    let _ = writeln!(io::stderr().lock(), "HORIZON_UVC {fields}");
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Config {
    pub name: String,
    pub serial: String,
    pub width: u16,
    pub height: u16,
    pub fps: u32,
    pub test_pattern: bool,
    /// Optional UAC1 microphone; silence until authenticated PCM is supplied.
    #[serde(default)]
    pub microphone: bool,
    /// Advertise one uncompressed YUY2 format instead of MJPEG.
    #[serde(default)]
    pub uncompressed: bool,
    /// Experimental fixed 640x480/30 H.264 stream carrying a public SEI marker.
    #[serde(default)]
    pub h264_probe: bool,
}
impl Default for Config {
    fn default() -> Self {
        Self {
            name: "4k web camera".into(),
            serial: "USBBridge-Camera-1".into(),
            width: 640,
            height: 480,
            fps: 30,
            test_pattern: true,
            microphone: false,
            uncompressed: false,
            h264_probe: false,
        }
    }
}
impl Config {
    pub fn validate(&self) -> io::Result<()> {
        if self.name.trim().is_empty()
            || self.name.encode_utf16().count() > 100
            || self.name.chars().any(char::is_control)
            || self.serial.is_empty()
            || self.serial.len() > 100
            || !self.serial.is_ascii()
            || self.serial.chars().any(char::is_control)
        {
            return Err(wire::invalid("camera name/serial is invalid"));
        }
        if !(16..=7680).contains(&self.width)
            || !(16..=4320).contains(&self.height)
            || !self.width.is_multiple_of(2)
            || !self.height.is_multiple_of(2)
            || !(1..=60).contains(&self.fps)
        {
            return Err(wire::invalid(
                "camera mode must be even width 16..7680, even height 16..4320 and 1..60 FPS",
            ));
        }
        if self.uncompressed
            && (self.raw_frame_size() > MAX_RAW_FRAME
                || u64::from(self.width) * u64::from(self.height) * 16 * u64::from(self.fps)
                    > u64::from(u32::MAX))
        {
            return Err(wire::invalid(
                "YUY2 frame or bitrate exceeds the UVC descriptor bound (4K up to 32 FPS; 8K up to 8 FPS)",
            ));
        }
        if self.h264_probe
            && (self.uncompressed || self.width != 640 || self.height != 480 || self.fps != 30)
        {
            return Err(wire::invalid(
                "H.264 probe requires RAW disabled and exactly 640x480 at 30 FPS",
            ));
        }
        Ok(())
    }
    pub fn interval(&self) -> u32 {
        10_000_000 / self.fps
    }
    fn intervals(&self) -> Vec<u32> {
        let mut intervals = vec![self.interval()];
        if !self.h264_probe {
            for fps in [15, 10, 5] {
                if fps <= self.fps {
                    let interval = 10_000_000 / fps;
                    if !intervals.contains(&interval) {
                        intervals.push(interval);
                    }
                }
            }
        }
        intervals.sort_unstable();
        intervals
    }
    fn normalize_interval(&self, requested: u32) -> Option<u32> {
        if requested == 0 {
            return Some(self.interval()); // Host has no frame-rate preference.
        }
        let nearest = self
            .intervals()
            .into_iter()
            .min_by_key(|interval| interval.abs_diff(requested))?;
        // Accommodate floor/round differences such as 666666 vs 666667,
        // without accepting arbitrary rates not listed in the descriptor.
        (nearest.abs_diff(requested) <= (nearest / 10_000).max(2)).then_some(nearest)
    }
    pub fn frame_limit(&self) -> usize {
        if self.h264_probe {
            H264_PROBE_FRAME.len()
        } else if self.uncompressed {
            self.raw_frame_size()
        } else {
            (usize::from(self.width) * usize::from(self.height) * 3 + 65536).min(MAX_JPEG)
        }
    }
    pub fn raw_frame_size(&self) -> usize {
        usize::from(self.width) * usize::from(self.height) * 2
    }
    fn device(&self) -> Device {
        Device {
            path: format!("/usbbridge/uvc/{}", self.serial),
            busid: BUSID.into(),
            bus: 7,
            address: 1,
            speed: 3,
            vendor: 0x1209,
            product: 0xf002,
            revision: 0x0100,
            class: 0xef,
            subclass: 2,
            protocol: 1,
            configuration: 1,
            configurations: 1,
            interfaces: if self.microphone {
                vec![[14, 1, 0, 0], [14, 2, 0, 0], [1, 1, 0, 0], [1, 2, 0, 0]]
            } else {
                vec![[14, 1, 0, 0], [14, 2, 0, 0]]
            },
        }
    }
    pub fn device_descriptor(&self) -> Vec<u8> {
        vec![
            18, 1, 0, 2, 0xef, 2, 1, 64, 9, 0x12, 2, 0xf0, 0, 1, 1, 2, 3, 1,
        ]
    }
    pub fn configuration_descriptor(&self) -> Vec<u8> {
        let intervals = self.intervals();
        let mut vc = vec![13, 0x24, 1, 0x10, 1, 40, 0];
        vc.extend(CLOCK.to_le_bytes());
        vc.extend([1, 1]);
        vc.extend([18, 0x24, 2, 1, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0]);
        vc.extend([9, 0x24, 3, 2, 1, 1, 0, 1, 0]);
        let mut vs = vec![14, 0x24, 1, 1, 0, 0, 0x81, 0, 2, 0, 0, 0, 1, 0];
        if self.h264_probe {
            // UVC 1.1 VS_FORMAT_FRAME_BASED, H264 FourCC GUID, variable size.
            vs.extend([28, 0x24, 0x10, 1, 1]);
            vs.extend([
                0x48, 0x32, 0x36, 0x34, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71,
            ]);
            vs.extend([0, 1, 0, 0, 0, 0, 1]);
        } else if self.uncompressed {
            vs.extend([27, 0x24, 4, 1, 1]);
            vs.extend([
                0x59, 0x55, 0x59, 0x32, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71,
            ]);
            vs.extend([16, 1, 0, 0, 0, 0]);
        } else {
            vs.extend([11, 0x24, 6, 1, 1, 1, 1, 0, 0, 0, 0]);
        }
        vs.extend([
            (26 + 4 * intervals.len()) as u8,
            0x24,
            if self.h264_probe {
                0x11
            } else if self.uncompressed {
                5
            } else {
                7
            },
            1,
            0,
        ]);
        vs.extend(self.width.to_le_bytes());
        vs.extend(self.height.to_le_bytes());
        let bitrate = (self.frame_limit() as u32)
            .saturating_mul(8)
            .saturating_mul(self.fps);
        let minimum_fps = 10_000_000 / intervals.last().copied().unwrap();
        let minimum_bitrate = (self.frame_limit() as u32)
            .saturating_mul(8)
            .saturating_mul(minimum_fps);
        vs.extend(minimum_bitrate.to_le_bytes());
        vs.extend(bitrate.to_le_bytes());
        if self.h264_probe {
            // VS_FRAME_FRAME_BASED uses a different field order from MJPEG/YUY2.
            vs.extend(self.interval().to_le_bytes());
            vs.push(intervals.len() as u8);
            vs.extend(0u32.to_le_bytes()); // Compressed stream: no bytes-per-line.
        } else {
            vs.extend((self.frame_limit() as u32).to_le_bytes());
            vs.extend(self.interval().to_le_bytes());
            vs.push(intervals.len() as u8);
        }
        for interval in intervals {
            vs.extend(interval.to_le_bytes());
        }
        vs.extend([6, 0x24, 0x0d, 1, 1, 4]);
        let streaming_length = vs.len() as u16;
        vs[4..6].copy_from_slice(&streaming_length.to_le_bytes());
        let length = 9 + 8 + 9 + vc.len() + 9 + vs.len() + 7;
        let mut result = vec![9, 2];
        result.extend((length as u16).to_le_bytes());
        result.extend([2, 1, 0, 0x80, 50]);
        result.extend([8, 11, 0, 2, 14, 3, 0, 2]);
        result.extend([9, 4, 0, 0, 0, 14, 1, 0, 0]);
        result.extend(vc);
        result.extend([9, 4, 1, 0, 1, 14, 2, 0, 0]);
        result.extend(vs);
        result.extend([7, 5, 0x81, 2, 0, 2, 0]);
        if self.microphone {
            result.extend(audio_descriptors());
            let length = result.len() as u16;
            result[2..4].copy_from_slice(&length.to_le_bytes());
            result[4] = 4;
        }
        result
    }
    fn other_speed_descriptor(&self) -> Vec<u8> {
        let mut descriptors = self.configuration_descriptor();
        descriptors[1] = 7;
        let mut offset = 0;
        while offset < descriptors.len() {
            let length = descriptors[offset] as usize;
            if descriptors[offset + 1] == 5 {
                match descriptors[offset + 2] {
                    0x81 => {
                        descriptors[offset + 4..offset + 6].copy_from_slice(&64u16.to_le_bytes())
                    }
                    0x82 => descriptors[offset + 6] = 1, // Full speed: 1 ms.
                    _ => {}
                }
            }
            offset += length;
        }
        descriptors
    }
    fn probe(&self) -> Vec<u8> {
        self.probe_at(self.interval())
    }
    fn probe_at(&self, interval: u32) -> Vec<u8> {
        let mut result = vec![0, 0, 1, 1];
        result.extend(interval.to_le_bytes());
        result.extend([0; 10]);
        result.extend((self.frame_limit() as u32).to_le_bytes());
        result.extend((PAYLOAD as u32).to_le_bytes());
        result.extend(CLOCK.to_le_bytes());
        result.extend([3, 1, 1, 1]);
        result
    }
    fn probe_resolution(&self) -> Vec<u8> {
        // Field resolution is in 100 ns units. The frame descriptor, not
        // GET_RES, defines the discrete supported rates; fixed fields are zero.
        let mut result = vec![0; 34];
        result[4..8].copy_from_slice(&1u32.to_le_bytes());
        result
    }
    fn string_descriptor(&self, index: u8) -> Option<Vec<u8>> {
        if index == 0 {
            return Some(vec![4, 3, 9, 4]);
        }
        let value = match index {
            1 => "USB to Network Bridge",
            2 => &self.name,
            3 => &self.serial,
            4 if self.microphone => "Microphone",
            _ => return None,
        };
        let mut result = vec![0, 3];
        for word in value.encode_utf16() {
            result.extend(word.to_le_bytes());
        }
        result[0] = result.len() as u8;
        Some(result)
    }
}

/// UAC1: microphone input terminal -> master-mute feature -> USB streaming
/// output terminal. HS bInterval=4 means 8 microframes = 1 ms, 48 mono i16
/// samples. The clock is synchronous to the emulated bus, with no feedback EP.
fn audio_descriptors() -> Vec<u8> {
    vec![
        8, 11, 2, 2, 1, 0, 0, 2, // Audio IAD: adapter keeps the camera product name.
        9, 4, 2, 0, 0, 1, 1, 0, 2, // AudioControl adapter name, not its microphone pin name.
        9, 0x24, 1, 0, 1, 39, 0, 1, 3, // UAC1 header: total CS AC length 39.
        12, 0x24, 2, 1, 1, 2, 0, 1, 0, 0, 0, 4, // Mono microphone input terminal 1.
        9, 0x24, 6, 2, 1, 1, 1, 0, 0, // Feature 2: master mute only.
        9, 0x24, 3, 3, 1, 1, 0, 2, 4, // USB streaming output terminal 3.
        9, 4, 3, 0, 0, 1, 2, 0, 4, // Default zero-bandwidth alt 0.
        9, 4, 3, 1, 1, 1, 2, 0, 4, // Operational AudioStreaming alt 1.
        7, 0x24, 1, 3, 1, 1, 0, // Terminal 3, PCM format tag.
        11, 0x24, 2, 1, 1, 2, 16, 1, 0x80, 0xbb, 0, // Mono PCM16, 48000 Hz.
        9, 5, 0x82, 0x0d, 96, 0, 4, 0, 0, // Synchronous isochronous IN.
        7, 0x25, 1, 1, 0, 0, 0, // Sampling-frequency control, no pitch.
    ]
}

/// Only baseline JPEG of the announced size is accepted before kernel delivery.
pub fn jpeg_size(bytes: &[u8]) -> io::Result<(u16, u16)> {
    if bytes.len() < 4 || bytes[..2] != [0xff, 0xd8] || bytes[bytes.len() - 2..] != [0xff, 0xd9] {
        return Err(wire::invalid("invalid JPEG envelope"));
    }
    let mut pos = 2;
    while pos + 4 <= bytes.len() && pos < 1024 * 1024 {
        if bytes[pos] != 0xff {
            return Err(wire::invalid("invalid JPEG marker"));
        }
        while pos < bytes.len() && bytes[pos] == 0xff {
            pos += 1;
        }
        let marker = *bytes
            .get(pos)
            .ok_or_else(|| wire::invalid("truncated JPEG marker"))?;
        pos += 1;
        if marker == 0xda || marker == 0xd9 {
            break;
        }
        if marker == 0x01 || (0xd0..=0xd7).contains(&marker) {
            continue;
        }
        let size = bytes
            .get(pos..pos + 2)
            .ok_or_else(|| wire::invalid("truncated JPEG length"))?;
        let length = usize::from(u16::from_be_bytes([size[0], size[1]]));
        if length < 2 || pos + length > bytes.len() {
            return Err(wire::invalid("JPEG segment outside frame"));
        }
        if marker == 0xc0 {
            if length < 8 || bytes[pos + 2] != 8 {
                return Err(wire::invalid("unsupported JPEG precision"));
            }
            return Ok((
                u16::from_be_bytes([bytes[pos + 5], bytes[pos + 6]]),
                u16::from_be_bytes([bytes[pos + 3], bytes[pos + 4]]),
            ));
        }
        if (0xc1..=0xcf).contains(&marker) && ![0xc4, 0xc8, 0xcc].contains(&marker) {
            return Err(wire::invalid("UVC input requires baseline JPEG"));
        }
        pos += length;
    }
    Err(wire::invalid("JPEG has no baseline frame header"))
}
/// A reused output buffer with a hard bound enforced while the encoder writes.
struct JpegOutput<'a> {
    bytes: &'a mut Vec<u8>,
    limit: usize,
}
impl io::Write for JpegOutput<'_> {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        let length = self
            .bytes
            .len()
            .checked_add(bytes.len())
            .filter(|length| *length <= self.limit)
            .ok_or_else(|| wire::invalid("encoded frame exceeds announced UVC size"))?;
        if length > self.bytes.capacity() {
            let capacity = length
                .max(self.bytes.capacity().saturating_mul(2))
                .min(self.limit);
            self.bytes
                .try_reserve_exact(capacity - self.bytes.len())
                .map_err(io::Error::other)?;
        }
        self.bytes.extend_from_slice(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
pub(crate) fn encode_bgra_into(
    pixels: &[u8],
    width: u16,
    height: u16,
    quality: u8,
    output: &mut Vec<u8>,
    limit: usize,
) -> io::Result<()> {
    if width == 0
        || height == 0
        || pixels.len() != usize::from(width) * usize::from(height) * 4
        || !(1..=100).contains(&quality)
        || limit == 0
        || limit > MAX_JPEG
    {
        return Err(wire::invalid("invalid JPEG image dimensions/quality/limit"));
    }
    output.clear();
    let result = jpeg_encoder::Encoder::new(
        JpegOutput {
            bytes: output,
            limit,
        },
        quality,
    )
    .encode(pixels, width, height, jpeg_encoder::ColorType::Bgra)
    .map_err(io::Error::other);
    if result.is_err() {
        output.clear();
    }
    result
}
pub fn encode_bgra(pixels: &[u8], width: u16, height: u16, quality: u8) -> io::Result<Vec<u8>> {
    let mut result = Vec::new();
    encode_bgra_into(pixels, width, height, quality, &mut result, MAX_JPEG)?;
    Ok(result)
}
fn yuy2_pair(first: &[u8], second: &[u8]) -> [u8; 4] {
    let luma = |pixel: &[u8]| {
        (((66 * i32::from(pixel[2]) + 129 * i32::from(pixel[1]) + 25 * i32::from(pixel[0]) + 128)
            >> 8)
            + 16)
            .clamp(0, 255) as u8
    };
    let b = (i32::from(first[0]) + i32::from(second[0]) + 1) / 2;
    let g = (i32::from(first[1]) + i32::from(second[1]) + 1) / 2;
    let r = (i32::from(first[2]) + i32::from(second[2]) + 1) / 2;
    let u = (((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128).clamp(0, 255) as u8;
    let v = (((112 * r - 94 * g - 18 * b + 128) >> 8) + 128).clamp(0, 255) as u8;
    [luma(first), u, luma(second), v]
}
pub(crate) fn encode_bgra_yuy2_into(
    pixels: &[u8],
    width: u16,
    height: u16,
    output: &mut Vec<u8>,
) -> io::Result<()> {
    let count = usize::from(width) * usize::from(height);
    if width == 0
        || height == 0
        || !width.is_multiple_of(2)
        || pixels.len() != count * 4
        || count * 2 > MAX_RAW_FRAME
    {
        return Err(wire::invalid("invalid YUY2 image dimensions or buffer"));
    }
    output.clear();
    if output.capacity() < count * 2 {
        output
            .try_reserve_exact(count * 2)
            .map_err(io::Error::other)?;
    }
    for pair in pixels.as_chunks::<8>().0 {
        output.extend(yuy2_pair(&pair[..4], &pair[4..]));
    }
    Ok(())
}
pub fn pattern(config: &Config, phase: usize, black: bool) -> io::Result<Vec<u8>> {
    config.validate()?;
    if config.h264_probe {
        return Ok(H264_PROBE_FRAME.to_vec());
    }
    let w = usize::from(config.width);
    let h = usize::from(config.height);
    let bars = [
        [235, 235, 235],
        [16, 235, 235],
        [235, 235, 16],
        [16, 235, 16],
        [235, 16, 235],
        [16, 16, 235],
        [235, 16, 16],
        [16, 16, 16],
    ];
    if config.uncompressed {
        let mut output = vec![0; config.raw_frame_size()];
        let color = |x: usize, y: usize| {
            if black {
                [0; 3]
            } else if y > h * 3 / 4 {
                if (x + phase * w / 12) % w < w / 12 {
                    [235; 3]
                } else {
                    [16; 3]
                }
            } else {
                bars[(x * 8 / w).min(7)]
            }
        };
        for y in 0..h {
            for x in (0..w).step_by(2) {
                let pair = yuy2_pair(&color(x, y), &color(x + 1, y));
                let offset = (y * w + x) * 2;
                output[offset..offset + 4].copy_from_slice(&pair);
            }
        }
        return Ok(output);
    }
    let mut pixels = vec![0; w * h * 4];
    if !black {
        for y in 0..h {
            for x in 0..w {
                let mut c = bars[(x * 8 / w).min(7)];
                if y > h * 3 / 4 {
                    c = if (x + phase * w / 12) % w < w / 12 {
                        [235; 3]
                    } else {
                        [16; 3]
                    };
                }
                let p = (y * w + x) * 4;
                pixels[p..p + 3].copy_from_slice(&c);
            }
        }
    }
    encode_bgra(&pixels, config.width, config.height, 80)
}
struct Latest {
    bytes: Arc<Vec<u8>>,
    received: Instant,
}
pub struct Frames {
    config: Config,
    latest: Mutex<Option<Latest>>,
    audio: Mutex<VecDeque<u8>>,
    patterns: Vec<Arc<Vec<u8>>>,
    black: Arc<Vec<u8>>,
    pub received: AtomicU64,
    pub supplied: AtomicU64,
}
impl Frames {
    pub fn new(config: Config) -> io::Result<Arc<Self>> {
        config.validate()?;
        let black = Arc::new(pattern(&config, 0, true)?);
        let patterns = if config.test_pattern && !config.h264_probe {
            (0..if config.uncompressed { 1 } else { 12 })
                .map(|n| pattern(&config, n, false).map(Arc::new))
                .collect::<io::Result<Vec<_>>>()?
        } else {
            Vec::new()
        };
        Ok(Arc::new(Self {
            config,
            latest: Mutex::new(None),
            audio: Mutex::new(VecDeque::new()),
            patterns,
            black,
            received: AtomicU64::new(0),
            supplied: AtomicU64::new(0),
        }))
    }
    pub fn config(&self) -> &Config {
        &self.config
    }
    pub fn accept(&self, bytes: Vec<u8>) -> io::Result<()> {
        if self.config.h264_probe {
            return Err(wire::invalid(
                "H.264 probe generates its own fixed video frame",
            ));
        }
        if self.config.uncompressed {
            if bytes.len() != self.config.raw_frame_size() {
                return Err(wire::invalid("YUY2 frame size differs from virtual camera"));
            }
        } else if bytes.len() > self.config.frame_limit()
            || jpeg_size(&bytes)? != (self.config.width, self.config.height)
        {
            return Err(wire::invalid("JPEG mode/size differs from virtual camera"));
        }
        *self.latest.lock().unwrap() = Some(Latest {
            bytes: Arc::new(bytes),
            received: Instant::now(),
        });
        self.received.fetch_add(1, Ordering::Relaxed);
        Ok(())
    }
    pub fn clear(&self) {
        *self.latest.lock().unwrap() = None;
        self.audio.lock().unwrap().clear();
    }
    pub fn accept_audio(&self, bytes: Vec<u8>) -> io::Result<()> {
        if !self.config.microphone
            || bytes.is_empty()
            || bytes.len() > MAX_AUDIO_CHUNK
            || !bytes.len().is_multiple_of(2)
        {
            return Err(wire::invalid("invalid or disabled 48 kHz mono PCM16 input"));
        }
        let mut audio = self.audio.lock().unwrap();
        let overflow = (audio.len() + bytes.len()).saturating_sub(MAX_AUDIO_QUEUE);
        if overflow > 0 {
            // A microphone that is not being read must not interrupt video.
            // Every queue/chunk size is even, preserving PCM sample boundaries.
            debug_assert!(overflow.is_multiple_of(2));
            drop(audio.drain(..overflow));
        }
        audio.extend(bytes);
        Ok(())
    }
    pub fn queued_audio_bytes(&self) -> usize {
        self.audio.lock().unwrap().len()
    }
    fn audio_into(&self, output: &mut [u8], muted: bool) {
        output.fill(0);
        let mut audio = self.audio.lock().unwrap();
        let available = output.len().min(audio.len()) & !1;
        for sample in output.iter_mut().take(available) {
            let byte = audio.pop_front().unwrap();
            if !muted {
                *sample = byte;
            }
        }
    }
    pub fn source_live(&self) -> bool {
        self.latest
            .lock()
            .unwrap()
            .as_ref()
            .is_some_and(|v| v.received.elapsed() < Duration::from_secs(2))
    }
    fn next(&self, index: usize) -> Arc<Vec<u8>> {
        if let Some(latest) = self
            .latest
            .lock()
            .unwrap()
            .as_ref()
            .filter(|v| v.received.elapsed() < Duration::from_secs(2))
        {
            return latest.bytes.clone();
        }
        self.patterns
            .get(index % self.patterns.len().max(1))
            .unwrap_or(&self.black)
            .clone()
    }
}
pub struct UvcProvider {
    frames: Arc<Frames>,
}
impl UvcProvider {
    pub fn new(frames: Arc<Frames>) -> Self {
        audit_uvc(
            serde_json::json!({"event":"uvc.video.advertised","width":frames.config.width,
            "height":frames.config.height,"configured_max_fps":frames.config.fps,
            "intervals_100ns":frames.config.intervals(),"frame_limit":frames.config.frame_limit(),
            "format":if frames.config.h264_probe {0x11}else if frames.config.uncompressed {5}else{7}}),
        );
        Self { frames }
    }
}
impl Provider for UvcProvider {
    fn devices(&self) -> io::Result<Vec<Device>> {
        Ok(vec![self.frames.config.device()])
    }
    fn open(&self, busid: &str, completed: SyncSender<Completion>) -> io::Result<Arc<dyn Session>> {
        if busid != BUSID {
            return Err(io::ErrorKind::NotFound.into());
        }
        let shared = Arc::new(Shared {
            state: Mutex::new(State {
                pending: VecDeque::new(),
                active: None,
                cancel_active: false,
                committed: false,
                probe_interval: self.frames.config.interval(),
                commit_interval: self.frames.config.interval(),
                video_generation: 0,
                control_traces: 0,
                audio: AudioState::default(),
            }),
            wake: Condvar::new(),
            stop: AtomicBool::new(false),
        });
        let signal = shared.clone();
        let frames = self.frames.clone();
        let output = completed.clone();
        let worker = thread::Builder::new()
            .name("uvc-bulk".into())
            .spawn(move || stream_frames(signal, frames, output))?;
        let audio_worker = if self.frames.config.microphone {
            let signal = shared.clone();
            let output = completed.clone();
            let frames = self.frames.clone();
            match thread::Builder::new()
                .name("uac-pcm".into())
                .spawn(move || stream_audio(signal, frames, output))
            {
                Ok(worker) => Some(worker),
                Err(error) => {
                    shared.stop.store(true, Ordering::Release);
                    shared.wake.notify_all();
                    let _ = worker.join();
                    return Err(error);
                }
            }
        } else {
            None
        };
        Ok(Arc::new(UvcSession {
            shared,
            completed,
            config: self.frames.config.clone(),
            worker: Mutex::new(Some(worker)),
            audio_worker: Mutex::new(audio_worker),
        }))
    }
}
struct State {
    pending: VecDeque<Submit>,
    active: Option<u32>,
    cancel_active: bool,
    committed: bool,
    probe_interval: u32,
    commit_interval: u32,
    video_generation: u64,
    control_traces: u32,
    audio: AudioState,
}
struct AudioState {
    pending: VecDeque<Submit>,
    active: Option<u32>,
    cancel_active: bool,
    generation: u64,
    alternate: u8,
    configuration: u8,
    muted: bool,
}
impl Default for AudioState {
    fn default() -> Self {
        Self {
            pending: VecDeque::new(),
            active: None,
            cancel_active: false,
            generation: 0,
            alternate: 0,
            configuration: 1,
            muted: false,
        }
    }
}
struct Shared {
    state: Mutex<State>,
    wake: Condvar,
    stop: AtomicBool,
}
struct UvcSession {
    shared: Arc<Shared>,
    completed: SyncSender<Completion>,
    config: Config,
    worker: Mutex<Option<thread::JoinHandle<()>>>,
    audio_worker: Mutex<Option<thread::JoinHandle<()>>>,
}
fn deliver(sender: &SyncSender<Completion>, stop: &AtomicBool, mut reply: Completion) -> bool {
    while !stop.load(Ordering::Acquire) {
        match sender.try_send(reply) {
            Ok(()) => return true,
            Err(TrySendError::Disconnected(_)) => return false,
            Err(TrySendError::Full(value)) => {
                reply = value;
                thread::sleep(Duration::from_millis(2));
            }
        }
    }
    false
}
impl UvcSession {
    fn video_control_trace(&self, operation: &str, requested: u32, selected: u32) {
        let mut state = self.shared.state.lock().unwrap();
        if state.control_traces >= 128
            || (state.control_traces >= 96
                && !matches!(operation, "commit_set" | "configuration_reset"))
        {
            return;
        }
        state.control_traces += 1;
        let fps = (selected > 0
            && !matches!(operation, "get_resolution" | "get_length" | "get_info"))
        .then(|| 10_000_000.0 / f64::from(selected));
        let fields = serde_json::json!({"event":"uvc.video.control","operation":operation,"requested_interval_100ns":requested,
                "selected_interval_100ns":selected,"selected_fps":fps,
                "committed":state.committed,"probe_interval_100ns":state.probe_interval,
                "commit_interval_100ns":state.commit_interval,"width":self.config.width,"height":self.config.height,
                "configured_max_fps":self.config.fps,"trace_index":state.control_traces});
        drop(state);
        audit_uvc(fields);
    }
    fn reset_video(&self) -> Option<()> {
        let pending = {
            let mut state = self.shared.state.lock().unwrap();
            state.committed = false;
            state.probe_interval = self.config.interval();
            state.commit_interval = self.config.interval();
            state.video_generation = state.video_generation.wrapping_add(1);
            state.pending.drain(..).collect::<Vec<_>>()
        };
        self.shared.wake.notify_all();
        for request in pending {
            if !deliver(
                &self.completed,
                &self.shared.stop,
                Completion::submit(&request, -108, 0, Vec::new(), 0, Vec::new()),
            ) {
                return None;
            }
        }
        self.video_control_trace("configuration_reset", 0, self.config.interval());
        Some(())
    }
    fn audio_alternate(&self, alternate: u8, configuration: Option<u8>) -> Option<Vec<u8>> {
        let pending = {
            let mut state = self.shared.state.lock().unwrap();
            let audio = &mut state.audio;
            if configuration.is_none() && audio.configuration == 0 {
                return None;
            }
            if let Some(value) = configuration {
                audio.configuration = value;
            }
            audio.alternate = alternate;
            audio.generation = audio.generation.wrapping_add(1);
            audio.pending.drain(..).collect::<Vec<_>>()
        };
        self.shared.wake.notify_all();
        crate::diagnostics::event(
            if alternate == 1 {
                "uac.start"
            } else {
                "uac.stop"
            },
            serde_json::json!({"rate":AUDIO_RATE,"channels":1,"bits":16,"silent":true}),
        );
        for request in pending {
            if !deliver(
                &self.completed,
                &self.shared.stop,
                audio_reply(&request, -108),
            ) {
                return None;
            }
        }
        Some(Vec::new())
    }
    fn audio_control(&self, request: &Submit) -> Option<Vec<u8>> {
        let s = request.setup;
        let value = u16::from_le_bytes([s[2], s[3]]);
        let index = u16::from_le_bytes([s[4], s[5]]);
        let length = u16::from_le_bytes([s[6], s[7]]) as usize;
        // Standard configuration and alternate settings affect only this audio
        // function; the original bulk-video probe/commit path is independent.
        match (s[0], s[1], value, index) {
            (0x00, 9, 0..=1, 0)
                if request.direction == 0 && length == 0 && request.data.is_empty() =>
            {
                return self.audio_alternate(0, Some(value as u8));
            }
            (0x80, 8, 0, 0) if request.direction == 1 => {
                return Some(vec![self.shared.state.lock().unwrap().audio.configuration]);
            }
            (0x81, 10, 0, 2) if request.direction == 1 => return Some(vec![0]),
            (0x81, 0, 0, 2 | 3) | (0x82, 0, 0, 0x82) if request.direction == 1 => {
                return Some(vec![0, 0]);
            }
            (0x81, 10, 0, 3) if request.direction == 1 => {
                return Some(vec![self.shared.state.lock().unwrap().audio.alternate]);
            }
            (0x01, 11, 0, 2) if request.direction == 0 && length == 0 => return Some(Vec::new()),
            (0x01, 11, 0..=1, 3) if request.direction == 0 && length == 0 => {
                return self.audio_alternate(value as u8, None);
            }
            _ => {}
        }
        // Feature unit 2 on AudioControl interface 2, master-channel mute.
        if index == 0x0202 && value == 0x0100 && length == 1 {
            match (s[0], s[1], request.direction) {
                (0xa1, 0x81, 1) => {
                    return Some(vec![u8::from(
                        self.shared.state.lock().unwrap().audio.muted,
                    )]);
                }
                (0x21, 1, 0) if request.data.len() == 1 && request.data[0] <= 1 => {
                    self.shared.state.lock().unwrap().audio.muted = request.data[0] != 0;
                    return Some(Vec::new());
                }
                _ => return None,
            }
        }
        // Fixed-rate sampling control on isochronous endpoint 0x82.
        // UAC1 5.2.3.2.3.1 rounds unsupported requested rates to the sole
        // supported value. PCM data stays silent regardless of master mute.
        if index == 0x0082 && value == 0x0100 && length == 3 {
            match (s[0], s[1], request.direction) {
                (0xa2, 0x81..=0x83, 1) => return Some(AUDIO_RATE.to_le_bytes()[..3].to_vec()),
                (0xa2, 0x84, 1) => return Some(vec![0, 0, 0]),
                (0x22, 1, 0) if request.data.len() == 3 && request.data[2] & 0x80 == 0 => {
                    return Some(Vec::new());
                }
                _ => return None,
            }
        }
        None
    }
    fn control(&self, request: &Submit) -> Option<Vec<u8>> {
        let s = request.setup;
        let value = u16::from_le_bytes([s[2], s[3]]);
        let index = u16::from_le_bytes([s[4], s[5]]);
        if s == [0, 9, 0, 0, 0, 0, 0, 0] && request.direction == 0 && request.data.is_empty() {
            self.reset_video()?;
        }
        if self.config.microphone
            && (index == 2
                || index == 3
                || index == 0x0202
                || index == 0x0082
                || matches!((s[0], s[1]), (0, 9) | (0x80, 8)))
        {
            // Descriptor requests still use the normal descriptor dispatcher.
            if !(s[0] == 0x80 && s[1] == 6) {
                return self.audio_control(request);
            }
        }
        let result = match s[0] & 0x60 {
            0 => match s[1] {
                6 => match (value >> 8) as u8 {
                    1 => self.config.device_descriptor(),
                    2 => self.config.configuration_descriptor(),
                    3 => self.config.string_descriptor(value as u8)?,
                    6 => vec![10, 6, 0, 2, 0xef, 2, 1, 64, 1, 0],
                    7 => self.config.other_speed_descriptor(),
                    _ => return None,
                },
                0 => vec![0, 0],
                8 => vec![1],
                9 if value <= 1 => Vec::new(),
                10 if index <= 1 => vec![0],
                11 if value == 0 && index <= 1 => Vec::new(),
                1 if s[0] & 0x1f == 2 => Vec::new(),
                _ => return None,
            },
            0x20 if index == 1 && value & 255 == 0 && [1, 2].contains(&((value >> 8) as u8)) => {
                match s[1] {
                    1 => {
                        let length = usize::from(u16::from_le_bytes([s[6], s[7]]));
                        if s[0] != 0x21
                            || request.direction != 0
                            || ![26, 34].contains(&request.data.len())
                            || length != request.data.len()
                            || request.data[2] != 1
                            || request.data[3] != 1
                        {
                            self.video_control_trace("set_rejected_metadata", 0, 0);
                            return None;
                        }
                        let requested = u32::from_le_bytes(request.data[4..8].try_into().unwrap());
                        let Some(interval) = self.config.normalize_interval(requested) else {
                            self.video_control_trace("set_rejected_interval", requested, 0);
                            return None;
                        };
                        let mut state = self.shared.state.lock().unwrap();
                        if value >> 8 == 2 {
                            state.commit_interval = interval;
                            state.committed = true;
                        } else {
                            state.probe_interval = interval;
                        }
                        drop(state);
                        self.shared.wake.notify_all();
                        self.video_control_trace(
                            if value >> 8 == 2 {
                                "commit_set"
                            } else {
                                "probe_set"
                            },
                            requested,
                            interval,
                        );
                        Vec::new()
                    }
                    0x81..=0x87
                        if s[0] == 0xa1 && request.direction == 1 && request.data.is_empty() =>
                    {
                        let intervals = self.config.intervals();
                        let interval = match s[1] {
                            0x81 => {
                                let state = self.shared.state.lock().unwrap();
                                if value >> 8 == 2 {
                                    state.commit_interval
                                } else {
                                    state.probe_interval
                                }
                            }
                            0x82 => intervals[0],
                            0x83 => *intervals.last().unwrap(),
                            _ => self.config.interval(),
                        };
                        self.video_control_trace(
                            match s[1] {
                                0x81 => "get_current",
                                0x82 => "get_min",
                                0x83 => "get_max",
                                0x84 => "get_resolution",
                                0x85 => "get_length",
                                0x86 => "get_info",
                                _ => "get_default",
                            },
                            0,
                            if s[1] == 0x84 { 1 } else { interval },
                        );
                        match s[1] {
                            0x84 => self.config.probe_resolution(),
                            0x85 => 34u16.to_le_bytes().to_vec(),
                            0x86 => vec![3],
                            0x87 => self.config.probe(),
                            _ => self.config.probe_at(interval),
                        }
                    }
                    _ => return None,
                }
            }
            0x20 if index == 0 && value == 0x0200 => match s[1] {
                0x81 => vec![0],
                0x86 => vec![1],
                _ => return None,
            },
            0x20 if index == 1 && value == 0x0600 => match s[1] {
                0x81 => vec![0],
                0x86 => vec![1],
                _ => return None,
            },
            _ => return None,
        };
        Some(result)
    }
}
impl Session for UvcSession {
    fn submit(&self, request: Submit) -> io::Result<()> {
        if request.endpoint == 0 {
            let reply = self.control(&request);
            let ok = reply.is_some();
            let mut bytes = reply.unwrap_or_default();
            bytes.truncate(request.length.min(usize::from(u16::from_le_bytes([
                request.setup[6],
                request.setup[7],
            ]))));
            let actual = if request.direction == 0 && ok {
                request.data.len()
            } else {
                bytes.len()
            };
            crate::diagnostics::event(
                "uvc.control",
                serde_json::json!({"setup":request.setup,"status":if ok{0}else{-32},"actual":actual}),
            );
            if !deliver(
                &self.completed,
                &self.shared.stop,
                Completion::submit(
                    &request,
                    if ok { 0 } else { -32 },
                    actual,
                    bytes,
                    0,
                    Vec::new(),
                ),
            ) {
                return Err(io::ErrorKind::BrokenPipe.into());
            }
        } else if request.endpoint == 1
            && request.direction == 1
            && request.iso.is_empty()
            && request.length >= 6
            && request.length <= 1024 * 1024
        {
            let mut state = self.shared.state.lock().unwrap();
            if state.pending.len() >= 128 {
                return Err(io::Error::other("UVC request queue limit reached"));
            }
            state.pending.push_back(request);
            self.shared.wake.notify_all();
        } else if request.endpoint == 2 && self.config.microphone {
            let status = {
                let mut state = self.shared.state.lock().unwrap();
                if !valid_audio_request(&request) {
                    -22
                } else if state.audio.alternate != 1 || state.audio.configuration == 0 {
                    -108
                } else if state.audio.pending.len() >= AUDIO_QUEUE {
                    -28
                } else {
                    state.audio.pending.push_back(request);
                    self.shared.wake.notify_all();
                    return Ok(());
                }
            };
            if !deliver(
                &self.completed,
                &self.shared.stop,
                audio_reply(&request, status),
            ) {
                return Err(io::ErrorKind::BrokenPipe.into());
            }
        } else if !deliver(
            &self.completed,
            &self.shared.stop,
            audio_reply(&request, -32),
        ) {
            return Err(io::ErrorKind::BrokenPipe.into());
        }
        Ok(())
    }
    fn cancel(&self, seq: u32, target: u32) -> io::Result<()> {
        let mut state = self.shared.state.lock().unwrap();
        let before = state.pending.len();
        state.pending.retain(|r| r.seq != target);
        let audio_before = state.audio.pending.len();
        state.audio.pending.retain(|r| r.seq != target);
        let found = before != state.pending.len()
            || state.active == Some(target)
            || audio_before != state.audio.pending.len()
            || state.audio.active == Some(target);
        if state.active == Some(target) {
            state.cancel_active = true;
        }
        if state.audio.active == Some(target) {
            state.audio.cancel_active = true;
        }
        drop(state);
        self.shared.wake.notify_all();
        if !deliver(
            &self.completed,
            &self.shared.stop,
            Completion::unlink(seq, if found { 0 } else { -2 }),
        ) {
            return Err(io::ErrorKind::BrokenPipe.into());
        }
        Ok(())
    }
}
impl Drop for UvcSession {
    fn drop(&mut self) {
        self.shared.stop.store(true, Ordering::Release);
        self.shared.wake.notify_all();
        if let Some(worker) = self.worker.lock().unwrap().take() {
            let _ = worker.join();
        }
        if let Some(worker) = self.audio_worker.lock().unwrap().take() {
            let _ = worker.join();
        }
    }
}

fn valid_audio_request(request: &Submit) -> bool {
    if request.direction != 1
        || request.iso.is_empty()
        || request.iso.len() > AUDIO_MAX_PACKETS
        || request.packet_count as usize != request.iso.len()
        || request.length > wire::MAX_TRANSFER
        || !request.data.is_empty()
    {
        return false;
    }
    let mut end = 0usize;
    for packet in &request.iso {
        let offset = packet.offset as usize;
        let Some(next) = offset.checked_add(packet.length as usize) else {
            return false;
        };
        if offset < end || next > request.length || packet.actual > packet.length {
            return false;
        }
        end = next;
    }
    true
}

/// USB/IP transmits packed payload, but the client requires each packet's
/// ORIGINAL offset and requested length. It reinserts the gaps in its URB.
fn audio_reply(request: &Submit, status: i32) -> Completion {
    let mut actual = 0usize;
    let iso = request
        .iso
        .iter()
        .map(|packet| {
            let bytes = if status == 0 {
                (packet.length as usize).min(AUDIO_PACKET) & !1
            } else {
                0
            };
            actual += bytes;
            wire::IsoPacket {
                offset: packet.offset,
                length: packet.length,
                actual: bytes as u32,
                status: if status == 0 && packet.length > 0 && packet.length < (AUDIO_PACKET as u32)
                {
                    -75
                } else {
                    status
                },
            }
        })
        .collect();
    // Only the compacted 96-byte packets are allocated, never the request's
    // potentially sparse original transfer buffer.
    Completion::submit(request, status, actual, vec![0; actual], 0, iso)
}

struct AudioClock {
    due: Instant,
    generation: u64,
}
impl AudioClock {
    fn new(now: Instant) -> Self {
        Self {
            due: now,
            generation: u64::MAX,
        }
    }
    fn reset(&mut self) {
        self.generation = u64::MAX;
    }
    fn deadline(
        &mut self,
        generation: u64,
        packets: usize,
        now: Instant,
        idle: Duration,
    ) -> Instant {
        // The Windows condition-variable timer can wake a 10 ms wait after
        // 15.6 ms. Re-anchoring on each late wake permanently loses samples.
        // Retain the USB clock and catch up bounded queued batches instead.
        if generation != self.generation
            || idle >= Duration::from_millis(100)
            || now.saturating_duration_since(self.due) > Duration::from_millis(250)
        {
            self.due = now;
            self.generation = generation;
        }
        self.due += Duration::from_millis(packets as u64);
        self.due
    }
}
fn stream_audio(shared: Arc<Shared>, frames: Arc<Frames>, completed: SyncSender<Completion>) {
    let mut clock = AudioClock::new(Instant::now());
    loop {
        let (request, generation, idle) = {
            let mut state = shared.state.lock().unwrap();
            let mut idle_since = None;
            while (state.audio.pending.is_empty() || state.audio.alternate != 1)
                && !shared.stop.load(Ordering::Acquire)
            {
                if state.audio.pending.is_empty() {
                    idle_since.get_or_insert_with(Instant::now);
                }
                state = shared.wake.wait(state).unwrap();
            }
            if shared.stop.load(Ordering::Acquire) {
                return;
            }
            let audio = &mut state.audio;
            let request = audio.pending.pop_front().unwrap();
            audio.active = Some(request.seq);
            audio.cancel_active = false;
            (
                request,
                audio.generation,
                idle_since.map(|since| since.elapsed()).unwrap_or_default(),
            )
        };
        let now = Instant::now();
        let due = clock.deadline(generation, request.iso.len(), now, idle);
        let mut state = shared.state.lock().unwrap();
        while Instant::now() < due
            && !shared.stop.load(Ordering::Acquire)
            && !state.audio.cancel_active
            && state.audio.generation == generation
        {
            let wait = due.saturating_duration_since(Instant::now());
            state = shared.wake.wait_timeout(state, wait).unwrap().0;
        }
        if shared.stop.load(Ordering::Acquire) {
            return;
        }
        if state.audio.cancel_active {
            state.audio.active = None;
            clock.reset();
            continue;
        }
        let mut status = if state.audio.generation == generation {
            0
        } else {
            -108
        };
        let mut reply = audio_reply(&request, status);
        if status == 0 {
            frames.audio_into(&mut reply.data, state.audio.muted);
        }
        // State lock serializes completion handoff with UNLINK. A successful
        // unlink can never be followed by RET_SUBMIT, including backpressure.
        loop {
            if shared.stop.load(Ordering::Acquire) {
                return;
            }
            if state.audio.cancel_active {
                state.audio.active = None;
                clock.reset();
                break;
            }
            if state.audio.generation != generation && status == 0 {
                status = -108;
                reply = audio_reply(&request, status);
            }
            if state.audio.muted {
                reply.data.fill(0);
            }
            match completed.try_send(reply) {
                Ok(()) => {
                    state.audio.active = None;
                    break;
                }
                Err(TrySendError::Disconnected(_)) => return,
                Err(TrySendError::Full(value)) => {
                    reply = value;
                    state = shared
                        .wake
                        .wait_timeout(state, Duration::from_millis(2))
                        .unwrap()
                        .0;
                }
            }
        }
    }
}
#[derive(Debug, PartialEq)]
enum VideoHandoff {
    Sent,
    Cancelled,
    Reset,
    Stopped,
}
fn complete_video(
    shared: &Shared,
    completed: &SyncSender<Completion>,
    request: &Submit,
    generation: u64,
    data: Vec<u8>,
) -> VideoHandoff {
    let mut state = shared.state.lock().unwrap();
    let mut status = 0;
    let mut reply = Completion::submit(request, 0, data.len(), data, 0, Vec::new());
    loop {
        if shared.stop.load(Ordering::Acquire) {
            return VideoHandoff::Stopped;
        }
        // A successful UNLINK always wins over reset and can never be followed
        // by RET_SUBMIT, including while the output channel is backpressured.
        if state.cancel_active {
            state.active = None;
            return VideoHandoff::Cancelled;
        }
        if (state.video_generation != generation || !state.committed) && status == 0 {
            status = -108;
            reply = Completion::submit(request, status, 0, Vec::new(), 0, Vec::new());
        }
        match completed.try_send(reply) {
            Ok(()) => {
                state.active = None;
                return if status == 0 {
                    VideoHandoff::Sent
                } else {
                    VideoHandoff::Reset
                };
            }
            Err(TrySendError::Disconnected(_)) => return VideoHandoff::Stopped,
            Err(TrySendError::Full(value)) => {
                reply = value;
                state = shared
                    .wake
                    .wait_timeout(state, Duration::from_millis(2))
                    .unwrap()
                    .0;
            }
        }
    }
}
fn stream_frames(shared: Arc<Shared>, frames: Arc<Frames>, completed: SyncSender<Completion>) {
    let start = Instant::now();
    let mut due = Instant::now();
    let mut current = Arc::new(Vec::new());
    let mut offset = 0;
    let mut number = 0usize;
    let mut presentation = 0u32;
    let mut generation = 0;
    let mut frame_interval = frames.config.interval();
    let mut last_frame_started = None;
    let mut trace = VideoTrace::new(&frames.config);
    loop {
        let (request, request_generation, selected_interval) = {
            let mut state = shared.state.lock().unwrap();
            while (state.pending.is_empty() || !state.committed)
                && !shared.stop.load(Ordering::Acquire)
            {
                state = shared.wake.wait(state).unwrap();
            }
            if shared.stop.load(Ordering::Acquire) {
                return;
            }
            let r = state.pending.pop_front().unwrap();
            state.active = Some(r.seq);
            state.cancel_active = false;
            (r, state.video_generation, state.commit_interval)
        };
        if request_generation != generation {
            generation = request_generation;
            offset = 0;
            due = Instant::now();
            last_frame_started = None;
        }
        if offset == 0 {
            if frame_interval != selected_interval
                && let Some(last) = last_frame_started
            {
                due = last + Duration::from_nanos(u64::from(selected_interval) * 100);
            }
            frame_interval = selected_interval;
            let period = Duration::from_nanos(u64::from(frame_interval) * 100);
            while Instant::now() < due && !shared.stop.load(Ordering::Acquire) {
                thread::sleep(
                    due.saturating_duration_since(Instant::now())
                        .min(Duration::from_millis(2)),
                );
            }
            if shared.stop.load(Ordering::Acquire) {
                return;
            }
            current = frames.next(number);
            last_frame_started = Some(Instant::now());
            presentation = (start.elapsed().as_nanos() * u128::from(CLOCK) / 1_000_000_000) as u32;
            let now = Instant::now();
            due = if now.saturating_duration_since(due) > period * 2 {
                now + period
            } else {
                due + period
            };
        }
        let count = (request.length.min(PAYLOAD) - 6).min(current.len() - offset);
        let end = offset + count == current.len();
        let mut data = Vec::with_capacity(count + 6);
        data.extend([6, 0x84 | ((number & 1) as u8) | if end { 2 } else { 0 }]);
        data.extend(presentation.to_le_bytes());
        data.extend_from_slice(&current[offset..offset + count]);
        match complete_video(&shared, &completed, &request, request_generation, data) {
            VideoHandoff::Stopped => return,
            VideoHandoff::Reset => {
                offset = 0;
                continue;
            }
            VideoHandoff::Cancelled => continue,
            VideoHandoff::Sent => {}
        }
        offset += count;
        if end {
            offset = 0;
            number = number.wrapping_add(1);
            frames.supplied.fetch_add(1, Ordering::Relaxed);
            trace.frame(frame_interval, current.len());
        }
    }
}

struct VideoTrace {
    width: u16,
    height: u16,
    started: Instant,
    last: Instant,
    frames: u64,
    bytes: u64,
    last_frames: u64,
    last_bytes: u64,
    interval: u32,
}
impl VideoTrace {
    fn new(config: &Config) -> Self {
        Self {
            width: config.width,
            height: config.height,
            started: Instant::now(),
            last: Instant::now(),
            frames: 0,
            bytes: 0,
            last_frames: 0,
            last_bytes: 0,
            interval: config.interval(),
        }
    }
    fn frame(&mut self, interval: u32, bytes: usize) {
        self.frames += 1;
        self.bytes += bytes as u64;
        self.interval = interval;
        if self.frames == 1 {
            audit_uvc(
                serde_json::json!({"event":"uvc.video.first_frame","width":self.width,"height":self.height,
                "interval_100ns":interval,"frame_bytes":bytes,"total_frames":1}),
            );
        }
        if self.last.elapsed() >= Duration::from_secs(1) {
            self.summary(false);
        }
    }
    fn summary(&mut self, final_event: bool) {
        let elapsed = self.last.elapsed().as_millis().max(1) as u64;
        audit_uvc(
            serde_json::json!({"event":"uvc.video.summary","width":self.width,"height":self.height,
            "interval_100ns":self.interval,"interval_ms":elapsed,"frames":self.frames-self.last_frames,
            "bytes":self.bytes-self.last_bytes,"total_frames":self.frames,"total_bytes":self.bytes,
            "elapsed_ms":self.started.elapsed().as_millis() as u64,"final":final_event}),
        );
        self.last = Instant::now();
        self.last_frames = self.frames;
        self.last_bytes = self.bytes;
    }
}
impl Drop for VideoTrace {
    fn drop(&mut self) {
        self.summary(true);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn multi_fps_session(config: Config) -> (UvcSession, std::sync::mpsc::Receiver<Completion>) {
        let (completed, rx) = std::sync::mpsc::sync_channel(16);
        let interval = config.interval();
        (
            UvcSession {
                shared: Arc::new(Shared {
                    state: Mutex::new(State {
                        pending: VecDeque::new(),
                        active: None,
                        cancel_active: false,
                        committed: false,
                        probe_interval: interval,
                        commit_interval: interval,
                        video_generation: 0,
                        control_traces: 0,
                        audio: AudioState::default(),
                    }),
                    wake: Condvar::new(),
                    stop: AtomicBool::new(false),
                }),
                completed,
                config,
                worker: Mutex::new(None),
                audio_worker: Mutex::new(None),
            },
            rx,
        )
    }
    fn multi_fps_control(
        session: &UvcSession,
        selector: u8,
        code: u8,
        data: Vec<u8>,
    ) -> Option<Vec<u8>> {
        let set = code == 1;
        let length = if set {
            data.len()
        } else if code == 0x85 {
            2
        } else if code == 0x86 {
            1
        } else {
            34
        };
        session.control(&request(
            0,
            0,
            u32::from(!set),
            length,
            [
                if set { 0x21 } else { 0xa1 },
                code,
                0,
                selector,
                1,
                0,
                length as u8,
                (length >> 8) as u8,
            ],
            data,
        ))
    }
    fn multi_fps_get_interval(session: &UvcSession, selector: u8, code: u8) -> u32 {
        let bytes = multi_fps_control(session, selector, code, Vec::new()).unwrap();
        u32::from_le_bytes(bytes[4..8].try_into().unwrap())
    }
    #[test]
    fn multi_fps_descriptors_and_bitrates_match_every_discrete_interval() {
        for uncompressed in [false, true] {
            for fps in [1, 5, 10, 15, 25, 30, 60] {
                let config = Config {
                    width: 64,
                    height: 48,
                    fps,
                    uncompressed,
                    ..Config::default()
                };
                config.validate().unwrap();
                let intervals = config.intervals();
                let bytes = config.configuration_descriptor();
                assert_eq!(
                    usize::from(u16::from_le_bytes(bytes[2..4].try_into().unwrap())),
                    bytes.len()
                );
                let mut offset = 0;
                let mut found = false;
                while offset < bytes.len() {
                    let length = usize::from(bytes[offset]);
                    assert!(length >= 2 && offset + length <= bytes.len());
                    let descriptor = &bytes[offset..offset + length];
                    if descriptor[1] == 0x24 && descriptor[2] == if uncompressed { 5 } else { 7 } {
                        assert_eq!(length, 26 + 4 * intervals.len());
                        assert_eq!(usize::from(descriptor[25]), intervals.len());
                        assert_eq!(
                            u32::from_le_bytes(descriptor[21..25].try_into().unwrap()),
                            config.interval()
                        );
                        let listed: Vec<_> = descriptor[26..]
                            .chunks_exact(4)
                            .map(|v| u32::from_le_bytes(v.try_into().unwrap()))
                            .collect();
                        assert_eq!(listed, intervals);
                        assert_eq!(
                            u32::from_le_bytes(descriptor[9..13].try_into().unwrap()),
                            config.frame_limit() as u32
                                * 8
                                * (10_000_000 / intervals.last().unwrap())
                        );
                        assert_eq!(
                            u32::from_le_bytes(descriptor[13..17].try_into().unwrap()),
                            config.frame_limit() as u32 * 8 * fps
                        );
                        found = true;
                    }
                    offset += length;
                }
                assert!(found);
                assert!(intervals.windows(2).all(|pair| pair[0] < pair[1]));
                assert!(
                    intervals
                        .iter()
                        .all(|interval| 10_000_000 / interval <= fps)
                );
            }
        }
    }
    #[test]
    fn multi_fps_near_rounding_is_normalized_without_fake_unsupported_rates() {
        let config = Config::default();
        assert_eq!(config.intervals(), [333333, 666666, 1000000, 2000000]);
        for interval in [666665, 666666, 666667] {
            assert_eq!(config.normalize_interval(interval), Some(666666));
        }
        assert_eq!(config.normalize_interval(0), Some(333333));
        for interval in [1, 10000, 500000, 3000000, u32::MAX] {
            assert_eq!(config.normalize_interval(interval), None);
        }
        let slow = Config {
            fps: 10,
            ..config.clone()
        };
        assert_eq!(slow.intervals(), [1000000, 2000000]);
        assert_eq!(slow.normalize_interval(666667), None);
        let h264 = Config {
            h264_probe: true,
            ..config
        };
        assert_eq!(h264.intervals(), [333333]);
        assert_eq!(h264.normalize_interval(666666), None);
    }
    #[test]
    fn multi_fps_probe_is_tentative_and_gets_never_start_or_change_commit() {
        let config = Config::default();
        let (session, _) = multi_fps_session(config.clone());
        assert!(multi_fps_control(&session, 1, 1, config.probe_at(666667)).is_some());
        assert_eq!(multi_fps_get_interval(&session, 1, 0x81), 666666);
        assert_eq!(multi_fps_get_interval(&session, 2, 0x81), 333333);
        for selector in [1, 2] {
            assert_eq!(multi_fps_get_interval(&session, selector, 0x82), 333333);
            assert_eq!(multi_fps_get_interval(&session, selector, 0x83), 2000000);
            assert_eq!(multi_fps_get_interval(&session, selector, 0x84), 1);
            assert_eq!(multi_fps_get_interval(&session, selector, 0x87), 333333);
            assert_eq!(
                multi_fps_control(&session, selector, 0x85, Vec::new()),
                Some(34u16.to_le_bytes().to_vec())
            );
            assert_eq!(
                multi_fps_control(&session, selector, 0x86, Vec::new()),
                Some(vec![3])
            );
        }
        let resolution = multi_fps_control(&session, 1, 0x84, Vec::new()).unwrap();
        assert_eq!(resolution.len(), 34);
        assert!(
            resolution
                .iter()
                .enumerate()
                .all(|(i, b)| *b == u8::from(i == 4))
        );
        let state = session.shared.state.lock().unwrap();
        assert!(!state.committed);
        assert_eq!(
            (state.probe_interval, state.commit_interval),
            (666666, 333333)
        );
    }
    #[test]
    fn multi_fps_commit_readback_and_legacy_26_byte_controls_work() {
        let config = Config::default();
        let (session, _) = multi_fps_session(config.clone());
        let mut legacy = config.probe_at(1000000);
        legacy.truncate(26);
        assert!(multi_fps_control(&session, 2, 1, legacy).is_some());
        assert_eq!(multi_fps_get_interval(&session, 2, 0x81), 1000000);
        assert_eq!(multi_fps_get_interval(&session, 1, 0x81), 333333);
        assert!(session.shared.state.lock().unwrap().committed);
        assert!(multi_fps_control(&session, 1, 1, config.probe_at(2000000)).is_some());
        assert_eq!(multi_fps_get_interval(&session, 2, 0x81), 1000000);
    }
    #[test]
    fn multi_fps_malformed_controls_stall_without_mutating_negotiation() {
        let config = Config::default();
        let (session, _) = multi_fps_session(config.clone());
        let mut bad_format = config.probe();
        bad_format[2] = 2;
        let mut bad_frame = config.probe();
        bad_frame[3] = 2;
        for bytes in [
            vec![],
            vec![0; 25],
            vec![0; 27],
            vec![0; 35],
            bad_format,
            bad_frame,
            config.probe_at(500000),
            config.probe_at(u32::MAX),
        ] {
            assert!(multi_fps_control(&session, 1, 1, bytes.clone()).is_none());
            assert!(multi_fps_control(&session, 2, 1, bytes).is_none());
        }
        assert!(
            session
                .control(&request(
                    0,
                    0,
                    0,
                    34,
                    [0x21, 1, 1, 1, 1, 0, 34, 0],
                    config.probe()
                ))
                .is_none()
        );
        assert!(
            session
                .control(&request(
                    0,
                    0,
                    0,
                    34,
                    [0xa1, 1, 0, 1, 1, 0, 34, 0],
                    config.probe()
                ))
                .is_none()
        );
        assert!(
            session
                .control(&request(
                    0,
                    0,
                    0,
                    34,
                    [0x21, 1, 0, 1, 1, 0, 26, 0],
                    config.probe()
                ))
                .is_none()
        );
        let state = session.shared.state.lock().unwrap();
        assert!(!state.committed);
        assert_eq!(
            (state.probe_interval, state.commit_interval),
            (333333, 333333)
        );
    }
    #[test]
    fn multi_fps_configuration_reset_retires_queued_video_and_resets_intervals() {
        let config = Config::default();
        let (session, rx) = multi_fps_session(config.clone());
        assert!(multi_fps_control(&session, 1, 1, config.probe_at(666666)).is_some());
        assert!(multi_fps_control(&session, 2, 1, config.probe_at(666666)).is_some());
        session
            .shared
            .state
            .lock()
            .unwrap()
            .pending
            .push_back(request(9, 1, 1, 512, [0; 8], Vec::new()));
        assert!(
            session
                .control(&request(0, 0, 0, 0, [0, 9, 0, 0, 0, 0, 0, 0], Vec::new()))
                .is_some()
        );
        let reply = rx.recv_timeout(Duration::from_millis(100)).unwrap();
        assert_eq!(wire::u32be(&reply.header[4..]), 9);
        assert_eq!(wire::u32be(&reply.header[20..]) as i32, -108);
        let state = session.shared.state.lock().unwrap();
        assert!(!state.committed && state.pending.is_empty());
        assert_eq!(
            (
                state.probe_interval,
                state.commit_interval,
                state.video_generation
            ),
            (333333, 333333, 1)
        );
    }
    #[test]
    fn multi_fps_usb_pacing_uses_commit_15_and_ignores_uncommitted_probe_5() {
        let config = Config {
            width: 16,
            height: 16,
            uncompressed: true,
            test_pattern: false,
            ..Config::default()
        };
        let frames = Frames::new(config.clone()).unwrap();
        let (tx, rx) = std::sync::mpsc::sync_channel(8);
        let session = UvcProvider::new(frames).open(BUSID, tx).unwrap();
        session
            .submit(request(
                1,
                0,
                0,
                34,
                [0x21, 1, 0, 2, 1, 0, 34, 0],
                config.probe_at(666667),
            ))
            .unwrap();
        assert_eq!(
            wire::u32be(&rx.recv_timeout(Duration::from_secs(1)).unwrap().header[20..]),
            0
        );
        session
            .submit(request(
                2,
                0,
                0,
                34,
                [0x21, 1, 0, 1, 1, 0, 34, 0],
                config.probe_at(2000000),
            ))
            .unwrap();
        assert_eq!(
            wire::u32be(&rx.recv_timeout(Duration::from_secs(1)).unwrap().header[20..]),
            0
        );
        let mut stamps = Vec::new();
        for seq in 3..9 {
            session
                .submit(request(seq, 1, 1, PAYLOAD, [0; 8], Vec::new()))
                .unwrap();
            let frame = rx.recv_timeout(Duration::from_secs(1)).unwrap();
            assert_ne!(frame.data[1] & 2, 0);
            assert_eq!(frame.data.len(), 518);
            stamps.push(u32::from_le_bytes(frame.data[2..6].try_into().unwrap()));
        }
        let elapsed = stamps.last().unwrap().wrapping_sub(stamps[0]);
        assert!(
            elapsed >= CLOCK * 29 / 100,
            "six frames at15fps must span at least290ms, got{}ticks",
            elapsed
        );
        assert!(
            elapsed < CLOCK * 9 / 10,
            "a tentative5fps Probe must not slow the committed15fps stream"
        );
    }
    #[test]
    fn multi_fps_cancel_and_reset_never_complete_an_unlinked_active_urb() {
        let (session, rx) = multi_fps_session(Config::default());
        {
            let mut state = session.shared.state.lock().unwrap();
            state.committed = true;
            state.active = Some(7);
            state.cancel_active = true;
            state.video_generation = 1;
        }
        assert_eq!(
            complete_video(
                &session.shared,
                &session.completed,
                &request(7, 1, 1, 512, [0; 8], Vec::new()),
                0,
                vec![0; 6]
            ),
            VideoHandoff::Cancelled
        );
        assert!(rx.try_recv().is_err());
        assert!(session.shared.state.lock().unwrap().active.is_none());
        // Exercise the same rule after a full completion channel forced the
        // worker to release its state lock and wait for cancellation/reset.
        let (tx, rx) = std::sync::mpsc::sync_channel(1);
        tx.send(Completion::unlink(99, 0)).unwrap();
        {
            let mut state = session.shared.state.lock().unwrap();
            state.committed = true;
            state.active = Some(8);
            state.cancel_active = false;
            state.video_generation = 0;
        }
        let signal = session.shared.clone();
        let worker = thread::spawn(move || {
            complete_video(
                &signal,
                &tx,
                &request(8, 1, 1, 512, [0; 8], Vec::new()),
                0,
                vec![0; 6],
            )
        });
        thread::sleep(Duration::from_millis(10));
        {
            let mut state = session.shared.state.lock().unwrap();
            state.cancel_active = true;
            state.video_generation = 1;
        }
        session.shared.wake.notify_all();
        assert_eq!(worker.join().unwrap(), VideoHandoff::Cancelled);
        assert_eq!(wire::u32be(&rx.recv().unwrap().header[4..]), 99);
        assert!(rx.try_recv().is_err());
    }
    #[test]
    fn h264_probe_descriptors_advertise_only_fixed_variable_size_h264() {
        let mut config = Config {
            h264_probe: true,
            ..Config::default()
        };
        config.validate().unwrap();
        let bytes = config.configuration_descriptor();
        assert_eq!(bytes.len(), 160);
        assert_eq!(
            usize::from(u16::from_le_bytes([bytes[2], bytes[3]])),
            bytes.len()
        );
        let mut position = 0;
        let mut format_count = 0;
        let mut frame_count = 0;
        while position < bytes.len() {
            let length = usize::from(bytes[position]);
            assert!(length >= 2 && position + length <= bytes.len());
            let descriptor = &bytes[position..position + length];
            if descriptor.len() >= 3 && descriptor[1] == 0x24 {
                match descriptor[2] {
                    0x10 => {
                        assert_eq!(length, 28);
                        assert_eq!(&descriptor[5..9], b"H264");
                        assert_eq!(descriptor[21], 0);
                        assert_eq!(descriptor[27], 1);
                        format_count += 1;
                    }
                    0x11 => {
                        assert_eq!(length, 30);
                        assert_eq!(
                            u16::from_le_bytes(descriptor[5..7].try_into().unwrap()),
                            640
                        );
                        assert_eq!(
                            u16::from_le_bytes(descriptor[7..9].try_into().unwrap()),
                            480
                        );
                        assert_eq!(
                            u32::from_le_bytes(descriptor[17..21].try_into().unwrap()),
                            config.interval()
                        );
                        assert_eq!(descriptor[21], 1);
                        assert_eq!(&descriptor[22..26], &[0; 4]);
                        assert_eq!(
                            u32::from_le_bytes(descriptor[26..30].try_into().unwrap()),
                            config.interval()
                        );
                        frame_count += 1;
                    }
                    4..=7 => panic!("probe must not advertise MJPEG or YUY2"),
                    _ => {}
                }
            }
            position += length;
        }
        assert_eq!((format_count, frame_count), (1, 1));
        assert_eq!(
            u32::from_le_bytes(config.probe()[18..22].try_into().unwrap()) as usize,
            H264_PROBE_FRAME.len()
        );
        config.width = 1280;
        assert!(config.validate().is_err());
        config.width = 640;
        config.fps = 29;
        assert!(config.validate().is_err());
        config.fps = 30;
        config.uncompressed = true;
        assert!(config.validate().is_err());
    }
    #[test]
    fn h264_probe_fixture_contains_parameter_sets_idr_and_public_sei_marker() {
        let starts: Vec<_> = H264_PROBE_FRAME
            .windows(3)
            .enumerate()
            .filter_map(|(position, value)| (value == [0, 0, 1]).then_some(position + 3))
            .collect();
        let mut types = Vec::new();
        let mut marker_found = false;
        let mut expected = vec![
            0x6b, 0x82, 0xd4, 0x3f, 0x4d, 0x4f, 0x4a, 0x2a, 0x99, 0x67, 0x0d, 0x62, 0x07, 0x61,
            0xc0, 0x01,
        ];
        expected.extend(b"HORIZON_SEI_PROBE_V1");
        for (index, start) in starts.iter().copied().enumerate() {
            let end = starts
                .get(index + 1)
                .map(|next| next - 3)
                .unwrap_or(H264_PROBE_FRAME.len());
            let nal = &H264_PROBE_FRAME[start..end];
            let kind = nal[0] & 31;
            types.push(kind);
            if kind == 6 && nal.windows(expected.len()).any(|value| value == expected) {
                marker_found = true;
            }
        }
        assert!(types.contains(&7) && types.contains(&8) && types.contains(&5));
        assert!(marker_found);
        let config = Config {
            h264_probe: true,
            ..Config::default()
        };
        let frames = Frames::new(config).unwrap();
        assert!(frames.patterns.is_empty());
        for index in [0, 1, 30, 1000] {
            assert_eq!(frames.next(index).as_slice(), H264_PROBE_FRAME);
        }
        assert!(frames.accept(H264_PROBE_FRAME.to_vec()).is_err());
    }
    #[test]
    fn h264_probe_usb_preserves_exact_annex_b_sei_and_frame_boundaries() {
        let config = Config {
            h264_probe: true,
            test_pattern: false,
            ..Config::default()
        };
        let frames = Frames::new(config.clone()).unwrap();
        let (tx, rx) = std::sync::mpsc::sync_channel(8);
        let session = UvcProvider::new(frames).open(BUSID, tx).unwrap();
        session
            .submit(request(
                1,
                0,
                0,
                34,
                [0x21, 1, 0, 2, 1, 0, 34, 0],
                config.probe(),
            ))
            .unwrap();
        assert_eq!(
            wire::u32be(&rx.recv_timeout(Duration::from_secs(1)).unwrap().header[20..]),
            0
        );
        let mut sequence = 2;
        let mut previous_fid = None;
        for _ in 0..2 {
            let mut received = Vec::new();
            let mut pts = None;
            let mut fid = None;
            loop {
                session
                    .submit(request(sequence, 1, 1, 512, [0; 8], Vec::new()))
                    .unwrap();
                sequence += 1;
                let reply = rx.recv_timeout(Duration::from_secs(1)).unwrap();
                assert_eq!(reply.data[0], 6);
                assert_eq!(
                    *pts.get_or_insert(reply.data[2..6].to_vec()),
                    reply.data[2..6]
                );
                assert_eq!(*fid.get_or_insert(reply.data[1] & 1), reply.data[1] & 1);
                received.extend_from_slice(&reply.data[6..]);
                assert!(received.len() <= H264_PROBE_FRAME.len());
                if reply.data[1] & 2 != 0 {
                    break;
                }
            }
            assert_eq!(received, H264_PROBE_FRAME);
            if let Some(previous) = previous_fid {
                assert_ne!(fid.unwrap(), previous);
            }
            previous_fid = fid;
        }
    }
    #[test]
    fn raw_yuy2_high_resolution_modes_keep_frame_and_dword_bitrate_bounds() {
        let mut config = Config {
            width: 3840,
            height: 2160,
            fps: 30,
            uncompressed: true,
            ..Config::default()
        };
        config.validate().unwrap();
        assert_eq!(config.frame_limit(), 16_588_800);
        config.fps = 32;
        config.validate().unwrap();
        config.fps = 60;
        assert!(config.validate().is_err());
        config.width = 7680;
        config.height = 4320;
        config.fps = 8;
        config.validate().unwrap();
        assert_eq!(config.frame_limit(), 66_355_200);
        assert!(config.frame_limit() <= MAX_RAW_FRAME);
        config.fps = 9;
        assert!(config.validate().is_err());
        config.uncompressed = false;
        config.fps = 60;
        config.validate().unwrap();
        assert_eq!(config.frame_limit(), MAX_JPEG);
        config.height = 4321;
        assert!(config.validate().is_err());
    }
    #[test]
    fn raw_yuy2_descriptors_advertise_one_uncompressed_format_and_exact_frame_size() {
        let config = Config {
            width: 64,
            height: 48,
            uncompressed: true,
            test_pattern: false,
            ..Config::default()
        };
        let bytes = config.configuration_descriptor();
        assert_eq!(bytes.len(), 171);
        assert_eq!(
            usize::from(u16::from_le_bytes([bytes[2], bytes[3]])),
            bytes.len()
        );
        let mut position = 0;
        let mut format_found = false;
        let mut frame_found = false;
        while position < bytes.len() {
            let length = usize::from(bytes[position]);
            assert!(length >= 2 && position + length <= bytes.len());
            let descriptor = &bytes[position..position + length];
            if descriptor.len() == 27 && descriptor[1..3] == [0x24, 4] {
                assert_eq!(
                    &descriptor[5..21],
                    &[
                        0x59, 0x55, 0x59, 0x32, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b,
                        0x71
                    ]
                );
                assert_eq!(descriptor[21], 16);
                format_found = true;
            }
            if descriptor.len() == 42 && descriptor[1..3] == [0x24, 5] {
                assert_eq!(
                    u32::from_le_bytes(descriptor[17..21].try_into().unwrap()),
                    6144
                );
                frame_found = true;
            }
            assert!(!(descriptor.len() == 11 && descriptor[1..3] == [0x24, 6]));
            position += length;
        }
        assert!(format_found && frame_found);
        assert_eq!(
            u32::from_le_bytes(config.probe()[18..22].try_into().unwrap()),
            6144
        );
        let legacy: Config = serde_json::from_value(serde_json::json!({"name":"Fixture","serial":"same","width":64,"height":48,"fps":30,"test_pattern":false})).unwrap();
        assert!(!legacy.uncompressed);
        assert_eq!(legacy.configuration_descriptor().len(), 155);
    }
    #[test]
    fn raw_yuy2_conversion_preserves_luma_chroma_and_reuses_bounded_storage() {
        let pixels = [
            0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 0, 255, 0, 255,
        ];
        let mut bytes = Vec::new();
        encode_bgra_yuy2_into(&pixels, 2, 2, &mut bytes).unwrap();
        assert_eq!(bytes, [16, 128, 235, 128, 82, 72, 144, 137]);
        let pointer = bytes.as_ptr();
        for _ in 0..20 {
            encode_bgra_yuy2_into(&pixels, 2, 2, &mut bytes).unwrap();
            assert_eq!(pointer, bytes.as_ptr());
        }
        assert!(encode_bgra_yuy2_into(&pixels, 3, 2, &mut bytes).is_err());
        assert!(encode_bgra_yuy2_into(&pixels[..4], 2, 2, &mut bytes).is_err());
        let config = Config {
            width: 64,
            height: 48,
            uncompressed: true,
            ..Config::default()
        };
        let frames = Frames::new(config).unwrap();
        assert_eq!(frames.patterns.len(), 1);
        assert!(
            frames
                .black
                .chunks_exact(4)
                .all(|pair| pair == [16, 128, 16, 128])
        );
        assert!(frames.accept(vec![0; 6143]).is_err());
        frames.accept(vec![0; 6144]).unwrap();
        assert!(frames.source_live());
    }
    #[test]
    fn raw_yuy2_usb_frame_is_complete_without_jpeg_encoding_or_decoding() {
        let config = Config {
            width: 64,
            height: 48,
            uncompressed: true,
            test_pattern: false,
            ..Config::default()
        };
        let frames = Frames::new(config.clone()).unwrap();
        let expected: Vec<u8> = (0..config.raw_frame_size()).map(|n| n as u8).collect();
        frames.accept(expected.clone()).unwrap();
        let (tx, rx) = std::sync::mpsc::sync_channel(8);
        let session = UvcProvider::new(frames).open(BUSID, tx).unwrap();
        session
            .submit(request(
                1,
                0,
                0,
                34,
                [0x21, 1, 0, 2, 1, 0, 34, 0],
                config.probe(),
            ))
            .unwrap();
        assert_eq!(
            wire::u32be(&rx.recv_timeout(Duration::from_secs(1)).unwrap().header[20..]),
            0
        );
        let mut raw = Vec::new();
        let mut pts = None;
        let mut fid = None;
        for seq in 2..30 {
            session
                .submit(request(seq, 1, 1, 1024, [0; 8], Vec::new()))
                .unwrap();
            let reply = rx.recv_timeout(Duration::from_secs(1)).unwrap();
            assert_eq!(reply.data[0], 6);
            assert_eq!(
                *pts.get_or_insert(reply.data[2..6].to_vec()),
                reply.data[2..6]
            );
            assert_eq!(*fid.get_or_insert(reply.data[1] & 1), reply.data[1] & 1);
            raw.extend_from_slice(&reply.data[6..]);
            if reply.data[1] & 2 != 0 {
                break;
            }
        }
        assert_eq!(raw, expected);
    }
    #[test]
    fn jpeg_output_is_bounded_during_encoding_and_reuses_storage() {
        let pixels = vec![127; 64 * 64 * 4];
        let mut output = Vec::new();
        encode_bgra_into(&pixels, 64, 64, 85, &mut output, 4096).unwrap();
        assert_eq!(jpeg_size(&output).unwrap(), (64, 64));
        let pointer = output.as_ptr();
        let capacity = output.capacity();
        for _ in 0..40 {
            encode_bgra_into(&pixels, 64, 64, 85, &mut output, 4096).unwrap();
            assert_eq!(output.as_ptr(), pointer);
            assert_eq!(output.capacity(), capacity);
        }
        let mut small = Vec::new();
        assert!(encode_bgra_into(&pixels, 64, 64, 100, &mut small, 64).is_err());
        assert!(small.is_empty());
        assert!(small.capacity() <= 64);
        assert!(encode_bgra_into(&[], 0, 64, 85, &mut small, 4096).is_err());
    }
    #[test]
    fn descriptors_have_valid_lengths_and_exact_mode() {
        let c = Config::default();
        let d = c.configuration_descriptor();
        assert_eq!(usize::from(u16::from_le_bytes([d[2], d[3]])), d.len());
        let mut p = 0;
        while p < d.len() {
            let n = usize::from(d[p]);
            assert!(n >= 2 && p + n <= d.len());
            p += n;
        }
        assert_eq!(p, d.len());
        assert_eq!(c.probe().len(), 34);
        assert!(d.windows(4).any(|b| b == [0x80, 2, 0xe0, 1]));
    }
    #[test]
    fn jpeg_dimensions_are_checked_before_kernel_delivery() {
        let c = Config::default();
        let bytes = pattern(&c, 0, false).unwrap();
        assert_eq!(jpeg_size(&bytes).unwrap(), (640, 480));
        let frames = Frames::new(c).unwrap();
        frames.accept(bytes).unwrap();
        assert!(frames.source_live());
        let mut bad = pattern(
            &Config {
                width: 320,
                height: 240,
                ..Config::default()
            },
            0,
            false,
        )
        .unwrap();
        assert!(frames.accept(bad.clone()).is_err());
        bad.truncate(20);
        assert!(jpeg_size(&bad).is_err());
        frames.clear();
        assert!(!frames.source_live());
    }
    #[test]
    fn invalid_modes_names_and_progressive_jpeg_are_rejected() {
        let mut c = Config {
            width: 641,
            ..Config::default()
        };
        assert!(c.validate().is_err());
        c.width = 640;
        c.name = "bad\0name".into();
        assert!(c.validate().is_err());
        assert!(jpeg_size(&[0xff, 0xd8, 0xff, 0xc2, 0, 8, 8, 0, 1, 0, 1, 1, 0xff, 0xd9]).is_err());
    }
    fn request(
        seq: u32,
        endpoint: u32,
        direction: u32,
        length: usize,
        setup: [u8; 8],
        data: Vec<u8>,
    ) -> Submit {
        Submit {
            seq,
            devid: (7 << 16) | 1,
            endpoint,
            direction,
            length,
            setup,
            data,
            flags: 0,
            start_frame: 0,
            packet_count: 0,
            interval: 0,
            iso: Vec::new(),
        }
    }
    #[test]
    fn silent_microphone_descriptors_and_legacy_camera_are_consistent() {
        let legacy: Config = serde_json::from_value(serde_json::json!({
            "name":"Fixture","serial":"same-serial","width":64,"height":48,
            "fps":30,"test_pattern":false
        }))
        .unwrap();
        assert!(!legacy.microphone);
        let mut enabled = legacy.clone();
        enabled.microphone = true;
        let base = legacy.configuration_descriptor();
        let descriptors = enabled.configuration_descriptor();
        assert_eq!(base.len(), 155);
        assert_eq!(descriptors.len(), base.len() + audio_descriptors().len());
        assert_eq!(
            u16::from_le_bytes([descriptors[2], descriptors[3]]) as usize,
            descriptors.len()
        );
        assert_eq!(descriptors[4], 4);
        let mut video_prefix = descriptors[..base.len()].to_vec();
        video_prefix[2..4].copy_from_slice(&base[2..4]);
        video_prefix[4] = 2;
        assert_eq!(video_prefix, base);
        assert_eq!(enabled.device_descriptor(), legacy.device_descriptor());
        assert_eq!(enabled.string_descriptor(3), legacy.string_descriptor(3));
        assert_eq!(enabled.string_descriptor(2), legacy.string_descriptor(2));
        let microphone = enabled.string_descriptor(4).unwrap();
        assert_eq!(
            String::from_utf16(
                &microphone[2..]
                    .as_chunks::<2>()
                    .0
                    .iter()
                    .map(|b| u16::from_le_bytes([b[0], b[1]]))
                    .collect::<Vec<_>>()
            )
            .unwrap(),
            "Microphone"
        );
        assert_eq!(audio_descriptors()[7], 2); // Adapter keeps the product name.
        assert_eq!(audio_descriptors()[16], 2);
        assert_eq!(
            enabled.device().interfaces,
            vec![[14, 1, 0, 0], [14, 2, 0, 0], [1, 1, 0, 0], [1, 2, 0, 0]]
        );
        let mut position = 0;
        let mut audio_endpoint = false;
        let other = enabled.other_speed_descriptor();
        while position < descriptors.len() {
            let length = descriptors[position] as usize;
            assert!(length >= 2 && position + length <= descriptors.len());
            let d = &descriptors[position..position + length];
            if d[1] == 5 && d[2] == 0x82 {
                assert_eq!(d, &[9, 5, 0x82, 0x0d, 96, 0, 4, 0, 0]);
                assert_eq!(other[position + 6], 1);
                audio_endpoint = true;
            }
            if d[1] == 5 && d[2] == 0x81 {
                assert_eq!(&other[position + 4..position + 6], &64u16.to_le_bytes());
            }
            position += length;
        }
        assert!(audio_endpoint);
        assert_eq!(
            &audio_descriptors()[17..26],
            &[9, 0x24, 1, 0, 1, 39, 0, 1, 3]
        );
        let mut original_other = legacy.configuration_descriptor();
        original_other[1] = 7;
        let n = original_other.len();
        original_other[n - 3..n - 1].copy_from_slice(&64u16.to_le_bytes());
        assert_eq!(legacy.other_speed_descriptor(), original_other);
    }
    fn audio_test_session(
        capacity: usize,
        worker: bool,
    ) -> (UvcSession, std::sync::mpsc::Receiver<Completion>) {
        let (completed, rx) = std::sync::mpsc::sync_channel(capacity);
        let shared = Arc::new(Shared {
            state: Mutex::new(State {
                pending: VecDeque::new(),
                active: None,
                cancel_active: false,
                committed: false,
                probe_interval: Config::default().interval(),
                commit_interval: Config::default().interval(),
                video_generation: 0,
                control_traces: 0,
                audio: AudioState::default(),
            }),
            wake: Condvar::new(),
            stop: AtomicBool::new(false),
        });
        let audio_worker = if worker {
            let signal = shared.clone();
            let output = completed.clone();
            let frames = Frames::new(Config {
                width: 64,
                height: 48,
                microphone: true,
                test_pattern: false,
                ..Config::default()
            })
            .unwrap();
            Some(thread::spawn(move || stream_audio(signal, frames, output)))
        } else {
            None
        };
        (
            UvcSession {
                shared,
                completed,
                config: Config {
                    microphone: true,
                    ..Config::default()
                },
                worker: Mutex::new(None),
                audio_worker: Mutex::new(audio_worker),
            },
            rx,
        )
    }
    #[test]
    fn pcm_microphone_queue_preserves_samples_zero_fills_and_honors_mute() {
        let frames = Frames::new(Config {
            width: 64,
            height: 48,
            microphone: true,
            test_pattern: false,
            ..Config::default()
        })
        .unwrap();
        for invalid in [vec![], vec![1], vec![1; MAX_AUDIO_CHUNK + 2]] {
            assert!(frames.accept_audio(invalid).is_err());
        }
        frames.accept_audio(vec![0x34, 0x12, 0xfe, 0xff]).unwrap();
        frames.accept_audio(vec![0x78, 0x56]).unwrap();
        let mut output = [9; 10];
        frames.audio_into(&mut output, false);
        assert_eq!(output, [0x34, 0x12, 0xfe, 0xff, 0x78, 0x56, 0, 0, 0, 0]);
        assert_eq!(frames.queued_audio_bytes(), 0);
        frames.accept_audio(vec![1, 2, 3, 4]).unwrap();
        frames.audio_into(&mut output[..2], true);
        assert_eq!(&output[..2], &[0, 0]);
        frames.audio_into(&mut output[..3], false);
        assert_eq!(&output[..3], &[3, 4, 0]);
        assert_eq!(frames.queued_audio_bytes(), 0);
        let disabled = Frames::new(Config {
            width: 64,
            height: 48,
            microphone: false,
            test_pattern: false,
            ..Config::default()
        })
        .unwrap();
        assert!(disabled.accept_audio(vec![0; 2]).is_err());
    }
    #[test]
    fn pcm_microphone_queue_is_bounded_and_disconnect_clears_pending_samples() {
        let frames = Frames::new(Config {
            width: 64,
            height: 48,
            microphone: true,
            test_pattern: false,
            ..Config::default()
        })
        .unwrap();
        for index in 0..MAX_AUDIO_QUEUE / MAX_AUDIO_CHUNK {
            let mut chunk = vec![1; MAX_AUDIO_CHUNK];
            if index == 0 {
                chunk[..4].copy_from_slice(&[0x11, 0x22, 0x33, 0x44]);
            }
            frames.accept_audio(chunk).unwrap();
        }
        assert_eq!(frames.queued_audio_bytes(), MAX_AUDIO_QUEUE);
        frames.accept_audio(vec![9, 10]).unwrap();
        assert_eq!(frames.queued_audio_bytes(), MAX_AUDIO_QUEUE);
        let mut packet = [0; AUDIO_PACKET];
        frames.audio_into(&mut packet, false);
        assert_eq!(&packet[..2], &[0x33, 0x44]);
        assert!(packet[2..].iter().all(|byte| *byte == 1));
        frames.accept_audio(vec![2; AUDIO_PACKET]).unwrap();
        assert_eq!(frames.queued_audio_bytes(), MAX_AUDIO_QUEUE);
        let mut retained = vec![0; MAX_AUDIO_QUEUE];
        frames.audio_into(&mut retained, false);
        assert_eq!(
            &retained[MAX_AUDIO_QUEUE - AUDIO_PACKET - 2..MAX_AUDIO_QUEUE - AUDIO_PACKET],
            &[9, 10]
        );
        assert_eq!(
            &retained[MAX_AUDIO_QUEUE - AUDIO_PACKET..],
            &[2; AUDIO_PACKET]
        );
        frames.accept_audio(vec![1, 2]).unwrap();
        frames.clear();
        assert_eq!(frames.queued_audio_bytes(), 0);
        frames.audio_into(&mut packet, false);
        assert_eq!(packet, [0; AUDIO_PACKET]);
    }
    #[test]
    fn pcm_microphone_usb_worker_delivers_injected_samples_then_silence() {
        let frames = Frames::new(Config {
            width: 64,
            height: 48,
            microphone: true,
            test_pattern: false,
            ..Config::default()
        })
        .unwrap();
        let pcm: Vec<u8> = (0..AUDIO_PACKET as u8).collect();
        frames.accept_audio(pcm.clone()).unwrap();
        let (tx, rx) = std::sync::mpsc::sync_channel(8);
        let session = UvcProvider::new(frames.clone()).open(BUSID, tx).unwrap();
        session
            .submit(request(1, 0, 0, 0, [1, 11, 1, 0, 3, 0, 0, 0], Vec::new()))
            .unwrap();
        let _ = rx.recv_timeout(Duration::from_secs(1)).unwrap();
        session.submit(audio_request(2, 1)).unwrap();
        let reply = rx.recv_timeout(Duration::from_secs(1)).unwrap();
        assert_eq!(reply.data, pcm);
        assert_eq!(reply.iso[0].actual, AUDIO_PACKET as u32);
        assert_eq!(frames.queued_audio_bytes(), 0);
        session.submit(audio_request(3, 1)).unwrap();
        assert_eq!(
            rx.recv_timeout(Duration::from_secs(1)).unwrap().data,
            vec![0; AUDIO_PACKET]
        );
        drop(session);
    }
    #[test]
    fn silent_microphone_clock_keeps_rate_when_windows_wakes_late() {
        let start = Instant::now();
        let mut clock = AudioClock::new(start);
        let mut now = start;
        let mut batches = 0u64;
        // Model a 15.625 ms Windows timer while the host queues 10 ms ISO
        // batches. Repeated relative sleeps lose ~36% of the nominal rate.
        let quantum = Duration::from_micros(15_625);
        while now.duration_since(start) < Duration::from_secs(5) {
            let deadline = clock.deadline(1, 10, now, Duration::ZERO);
            if deadline > now {
                let wait = deadline.duration_since(now).as_nanos();
                let ticks = wait.div_ceil(quantum.as_nanos()) as u32;
                now += quantum * ticks;
            }
            batches += 1;
        }
        let wall = now.duration_since(start);
        let audio = Duration::from_millis(batches * 10);
        assert!(
            wall.saturating_sub(audio) <= quantum,
            "late wakes must not reduce the sustained sample clock"
        );
        assert_eq!(clock.due, start + audio);
        // Explicit stop/cancel, a real input-queue idle period and a large
        // scheduling gap re-anchor; they must not dump old batches forever.
        clock.reset();
        assert_eq!(
            clock.deadline(1, 10, now, Duration::ZERO),
            now + Duration::from_millis(10)
        );
        now += Duration::from_millis(150);
        assert_eq!(
            clock.deadline(1, 10, now, Duration::from_millis(120)),
            now + Duration::from_millis(10)
        );
        now += Duration::from_millis(300);
        assert_eq!(
            clock.deadline(1, 10, now, Duration::ZERO),
            now + Duration::from_millis(10)
        );
        now += Duration::from_millis(5);
        assert_eq!(
            clock.deadline(2, 10, now, Duration::ZERO),
            now + Duration::from_millis(10)
        );
    }
    fn audio_request(seq: u32, count: usize) -> Submit {
        let mut r = request(seq, 2, 1, count * AUDIO_PACKET, [0; 8], Vec::new());
        r.packet_count = count as u32;
        r.interval = 8;
        r.iso = (0..count)
            .map(|n| wire::IsoPacket {
                offset: (n * AUDIO_PACKET) as u32,
                length: AUDIO_PACKET as u32,
                ..wire::IsoPacket::default()
            })
            .collect();
        r
    }
    fn set_audio_alt(session: &UvcSession, alt: u8) {
        assert!(
            session
                .control(&request(
                    900,
                    0,
                    0,
                    0,
                    [1, 11, alt, 0, 3, 0, 0, 0],
                    Vec::new()
                ))
                .is_some()
        );
    }
    #[test]
    fn silent_microphone_controls_match_advertised_rate_mute_and_alternates() {
        let (session, _rx) = audio_test_session(8, false);
        assert_eq!(
            session.control(&request(
                1,
                0,
                1,
                1,
                [0x81, 10, 0, 0, 3, 0, 1, 0],
                Vec::new()
            )),
            Some(vec![0])
        );
        set_audio_alt(&session, 1);
        assert_eq!(
            session.control(&request(
                1,
                0,
                1,
                1,
                [0x81, 10, 0, 0, 3, 0, 1, 0],
                Vec::new()
            )),
            Some(vec![1])
        );
        for code in 0x81..=0x83 {
            assert_eq!(
                session.control(&request(
                    2,
                    0,
                    1,
                    3,
                    [0xa2, code, 0, 1, 0x82, 0, 3, 0],
                    Vec::new()
                )),
                Some(vec![0x80, 0xbb, 0])
            );
        }
        assert_eq!(
            session.control(&request(
                2,
                0,
                1,
                3,
                [0xa2, 0x84, 0, 1, 0x82, 0, 3, 0],
                Vec::new()
            )),
            Some(vec![0, 0, 0])
        );
        assert!(
            session
                .control(&request(
                    2,
                    0,
                    0,
                    3,
                    [0x22, 1, 0, 1, 0x82, 0, 3, 0],
                    vec![0x44, 0xac, 0]
                ))
                .is_some()
        );
        assert_eq!(
            session.control(&request(
                2,
                0,
                1,
                3,
                [0xa2, 0x81, 0, 1, 0x82, 0, 3, 0],
                Vec::new()
            )),
            Some(vec![0x80, 0xbb, 0])
        );
        assert!(
            session
                .control(&request(3, 0, 0, 1, [0x21, 1, 0, 1, 2, 2, 1, 0], vec![1]))
                .is_some()
        );
        assert_eq!(
            session.control(&request(
                4,
                0,
                1,
                1,
                [0xa1, 0x81, 0, 1, 2, 2, 1, 0],
                Vec::new()
            )),
            Some(vec![1])
        );
        assert!(
            session
                .control(&request(5, 0, 0, 1, [0x21, 1, 1, 1, 2, 2, 1, 0], vec![1]))
                .is_none()
        );
        assert!(
            session
                .control(&request(6, 0, 0, 0, [1, 11, 2, 0, 3, 0, 0, 0], Vec::new()))
                .is_none()
        );
        assert!(
            session
                .control(&request(7, 0, 0, 0, [0, 9, 0, 0, 0, 0, 0, 0], Vec::new()))
                .is_some()
        );
        assert_eq!(
            session.control(&request(
                8,
                0,
                1,
                1,
                [0x80, 8, 0, 0, 0, 0, 1, 0],
                Vec::new()
            )),
            Some(vec![0])
        );
        assert!(
            session
                .control(&request(9, 0, 0, 0, [1, 11, 1, 0, 3, 0, 0, 0], Vec::new()))
                .is_none()
        );
    }
    #[test]
    fn silent_microphone_iso_is_paced_compacted_and_independent_of_video() {
        let frames = Frames::new(Config {
            width: 64,
            height: 48,
            microphone: true,
            test_pattern: false,
            ..Config::default()
        })
        .unwrap();
        assert!(!frames.source_live());
        let (tx, rx) = std::sync::mpsc::sync_channel(8);
        let session = UvcProvider::new(frames).open(BUSID, tx).unwrap();
        session
            .submit(request(1, 0, 0, 0, [1, 11, 1, 0, 3, 0, 0, 0], Vec::new()))
            .unwrap();
        assert_eq!(
            wire::u32be(&rx.recv_timeout(Duration::from_secs(1)).unwrap().header[20..]),
            0
        );
        let mut req = audio_request(2, 3);
        req.length = 768;
        req.iso = vec![
            wire::IsoPacket {
                offset: 0,
                length: 128,
                ..Default::default()
            },
            wire::IsoPacket {
                offset: 128,
                length: 128,
                ..Default::default()
            },
            wire::IsoPacket {
                offset: 512,
                length: 256,
                ..Default::default()
            },
        ];
        assert!(valid_audio_request(&req));
        let started = Instant::now();
        session.submit(req).unwrap();
        let reply = rx.recv_timeout(Duration::from_secs(1)).unwrap();
        assert!(started.elapsed() >= Duration::from_millis(2));
        assert_eq!(reply.data, vec![0; 288]);
        assert_eq!(wire::u32be(&reply.header[24..]), 288);
        assert_eq!(wire::u32be(&reply.header[36..]), 0);
        assert_eq!(
            reply
                .iso
                .iter()
                .map(|p| (p.offset, p.length, p.actual, p.status))
                .collect::<Vec<_>>(),
            vec![(0, 128, 96, 0), (128, 128, 96, 0), (512, 256, 96, 0)]
        );
        let mut bytes = Vec::new();
        reply.write(&mut bytes).unwrap();
        assert_eq!(bytes.len(), 48 + 288 + 48);
        assert_eq!(wire::u32be(&bytes[48 + 288 + 32..]), 512);
        let mut bad = audio_request(3, 2);
        bad.iso[1].offset = 1;
        assert!(!valid_audio_request(&bad));
        session.submit(bad).unwrap();
        let rejected = rx.recv_timeout(Duration::from_secs(1)).unwrap();
        assert_eq!(wire::u32be(&rejected.header[20..]) as i32, -22);
        assert_eq!(rejected.iso.len(), 2);
        assert!(
            rejected
                .iso
                .iter()
                .all(|p| p.actual == 0 && p.status == -22)
        );
        assert!(!valid_audio_request(&audio_request(
            4,
            AUDIO_MAX_PACKETS + 1
        )));
    }
    #[test]
    fn silent_microphone_unlink_stop_restart_and_full_queue_shutdown_drain() {
        let (session, rx) = audio_test_session(1, true);
        set_audio_alt(&session, 1);
        session.submit(audio_request(1, 512)).unwrap();
        let deadline = Instant::now() + Duration::from_secs(1);
        while session.shared.state.lock().unwrap().audio.active != Some(1) {
            assert!(Instant::now() < deadline);
            thread::yield_now();
        }
        session.submit(audio_request(2, 1)).unwrap();
        session.cancel(3, 2).unwrap();
        assert_eq!(
            wire::u32be(&rx.recv_timeout(Duration::from_secs(1)).unwrap().header),
            wire::RET_UNLINK
        );
        session.cancel(4, 1).unwrap();
        let cancel = rx.recv_timeout(Duration::from_secs(1)).unwrap();
        assert_eq!(wire::u32be(&cancel.header), wire::RET_UNLINK);
        assert_eq!(wire::u32be(&cancel.header[20..]), 0);
        assert!(rx.recv_timeout(Duration::from_millis(10)).is_err());
        session.submit(audio_request(5, 1)).unwrap();
        assert_eq!(
            wire::u32be(&rx.recv_timeout(Duration::from_millis(100)).unwrap().header[4..]),
            5
        );
        session.submit(audio_request(6, 512)).unwrap();
        let deadline = Instant::now() + Duration::from_secs(1);
        while session.shared.state.lock().unwrap().audio.active != Some(6) {
            assert!(Instant::now() < deadline);
            thread::yield_now();
        }
        set_audio_alt(&session, 0);
        let stopped = rx.recv_timeout(Duration::from_millis(100)).unwrap();
        assert_eq!(wire::u32be(&stopped.header[4..]), 6);
        assert_eq!(wire::u32be(&stopped.header[20..]) as i32, -108);
        set_audio_alt(&session, 1);
        session.submit(audio_request(7, 1)).unwrap();
        thread::sleep(Duration::from_millis(5)); // Leave the bounded output full.
        session.submit(audio_request(8, 1)).unwrap();
        thread::sleep(Duration::from_millis(5));
        let started = Instant::now();
        drop(session);
        assert!(started.elapsed() < Duration::from_millis(500));
    }
    #[test]
    fn usb_frame_is_complete_jpeg_with_stable_timestamp_and_frame_boundary() {
        let frames = Frames::new(Config {
            width: 64,
            height: 48,
            ..Config::default()
        })
        .unwrap();
        let (tx, rx) = std::sync::mpsc::sync_channel(8);
        let session = UvcProvider::new(frames).open(BUSID, tx).unwrap();
        let mut probe = Config::default().probe();
        probe[2] = 1;
        probe[3] = 1;
        session
            .submit(request(1, 0, 0, 34, [0x21, 1, 0, 2, 1, 0, 34, 0], probe))
            .unwrap();
        assert_eq!(
            wire::u32be(&rx.recv_timeout(Duration::from_secs(1)).unwrap().header[20..]),
            0
        );
        let mut jpeg = Vec::new();
        let mut pts = None;
        let mut fid = None;
        for seq in 2..200 {
            session
                .submit(request(seq, 1, 1, 512, [0; 8], Vec::new()))
                .unwrap();
            let reply = rx.recv_timeout(Duration::from_secs(1)).unwrap();
            let data = &reply.data;
            assert_eq!(data[0], 6);
            assert_eq!(*pts.get_or_insert(data[2..6].to_vec()), data[2..6]);
            assert_eq!(*fid.get_or_insert(data[1] & 1), data[1] & 1);
            jpeg.extend_from_slice(&data[6..]);
            if data[1] & 2 != 0 {
                break;
            }
        }
        assert_eq!(jpeg_size(&jpeg).unwrap(), (64, 48));
        session
            .submit(request(201, 1, 1, PAYLOAD, [0; 8], Vec::new()))
            .unwrap();
        let next = rx.recv_timeout(Duration::from_secs(1)).unwrap();
        assert_ne!(next.data[1] & 1, fid.unwrap());
    }
    #[test]
    fn unlink_removes_queued_request_without_later_completion() {
        let frames = Frames::new(Config {
            width: 64,
            height: 48,
            ..Config::default()
        })
        .unwrap();
        let (tx, rx) = std::sync::mpsc::sync_channel(4);
        let session = UvcProvider::new(frames).open(BUSID, tx).unwrap();
        session
            .submit(request(1, 1, 1, 512, [0; 8], Vec::new()))
            .unwrap();
        session.cancel(2, 1).unwrap();
        let reply = rx.recv_timeout(Duration::from_secs(1)).unwrap();
        assert_eq!(wire::u32be(&reply.header), wire::RET_UNLINK);
        assert!(rx.recv_timeout(Duration::from_millis(30)).is_err());
    }
    #[test]
    fn stale_network_image_is_replaced_by_black_without_removing_camera() {
        let frames = Frames::new(Config {
            width: 64,
            height: 48,
            test_pattern: false,
            ..Config::default()
        })
        .unwrap();
        let image = pattern(frames.config(), 0, false).unwrap();
        *frames.latest.lock().unwrap() = Some(Latest {
            bytes: Arc::new(image),
            received: Instant::now() - Duration::from_secs(3),
        });
        assert!(!frames.source_live());
        assert_eq!(*frames.next(0), *frames.black);
        assert_eq!(UvcProvider::new(frames).devices().unwrap().len(), 1);
    }
}
