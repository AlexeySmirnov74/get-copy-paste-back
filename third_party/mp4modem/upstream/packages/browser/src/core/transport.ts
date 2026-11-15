import {
  ByteWriter,
  equalBytes,
  readU16,
  readU32,
  readU64,
  utf8,
} from "./binary.js";
import {
  FRAME_DATA_BYTES,
  PROFILE_ID,
  TRANSPORT_VERSION,
  decodeBasisFrame,
  encodeBasisFrame,
} from "./basis.js";
import type { CompactTag } from "./basis.js";
import { bytesToHex, crc32, sha256 } from "./checksum.js";
import {
  FEC_PAYLOAD_SIZE,
  FEC_SYMBOLS,
  RS_BLOCK_SIZE,
  decodeFec,
  encodeFec,
} from "./fec.js";

export const DEFAULT_PROFILE = "v2-4basis-bpsk-16";
export const FPS = 30;
export const CONTROL_FLAG = 0x01;
export const CONTROL_RECORD_BYTES = 96;
export const CONTROL_COPIES = 17;
export const DATA_BYTES_PER_FRAME = FRAME_DATA_BYTES - 4;
export const DATA_FRAMES_PER_SUPERFRAME = 119;
export const MAXIMUM_BUNDLE_BYTES = 20 * 1024 * 1024;
const CONTROL_MAGIC = utf8("M4V2");
const LEGACY_CONTROL_MAGIC = utf8("FPV2");

export interface ControlRecord {
  profileId: number;
  packageId: Uint8Array;
  originalSize: number;
  encodedSize: number;
  dataFrames: number;
  fecSymbols: number;
  dataBytesPerFrame: number;
  bundleSha256: Uint8Array;
}

export interface TransportEstimate {
  profile: string;
  transportVersion: 2;
  frameCount: number;
  controlFrames: number;
  fecBlocks: number;
  encodedBytes: number;
  videoFrameCount: number;
  durationSeconds: number;
}

export interface EncodedTransport extends TransportEstimate {
  bundleBytes: number;
  bundleSha256: string;
  frames: Iterable<Uint8Array>;
}

export interface DecodedTransport {
  bundle: Uint8Array;
  diagnostics: {
    path: "video-v2";
    transportVersion: 2;
    profile: string;
    decodedVideoFrames: number;
    readableTags: number;
    readableControlFrames: number;
    recoveredDataFrames: number;
    crcValidDataFrames: number;
    expectedDataFrames: number;
    missingDataFrames: number;
    crcFailedDataFrames: number;
    erasedBytes: number;
    minimumPilotSeparation: number | null;
    bundleBytes: number;
    bundleSha256: string;
  };
}

export function durationToFrames(seconds: number): number {
  if (!Number.isFinite(seconds) || seconds < 0)
    throw new Error("Duration must be finite and non-negative");
  return Math.floor(seconds * FPS + 0.5);
}

export function estimateTransport(
  bundleSize: number,
  coverSeconds = 1.5,
  endCoverSeconds = 0.5,
): TransportEstimate {
  if (!Number.isSafeInteger(bundleSize) || bundleSize < 0)
    throw new Error("Bundle size must be a non-negative safe integer");
  const fecBlocks = Math.max(1, Math.ceil(bundleSize / FEC_PAYLOAD_SIZE));
  const encodedBytes = fecBlocks * RS_BLOCK_SIZE;
  const frameCount = Math.ceil(encodedBytes / DATA_BYTES_PER_FRAME);
  const controlFrames = Math.ceil(frameCount / DATA_FRAMES_PER_SUPERFRAME);
  const videoFrameCount =
    durationToFrames(coverSeconds) +
    frameCount +
    controlFrames +
    durationToFrames(endCoverSeconds);
  return {
    profile: DEFAULT_PROFILE,
    transportVersion: 2,
    frameCount,
    controlFrames,
    fecBlocks,
    encodedBytes,
    videoFrameCount,
    durationSeconds: videoFrameCount / FPS,
  };
}

export function maximumBundleBytes(
  maximumDurationSeconds = 120,
  coverSeconds = 1.5,
  endCoverSeconds = 0.5,
): number {
  if (!Number.isFinite(maximumDurationSeconds) || maximumDurationSeconds <= 0)
    throw new Error("Maximum duration must be positive");
  const maximumFrames = Math.floor(maximumDurationSeconds * FPS);
  let low = 0;
  let high = MAXIMUM_BUNDLE_BYTES;
  while (low < high) {
    const middle = Math.floor((low + high + 1) / 2);
    if (
      estimateTransport(middle, coverSeconds, endCoverSeconds)
        .videoFrameCount <= maximumFrames
    )
      low = middle;
    else high = middle - 1;
  }
  return low;
}

export function packControl(record: ControlRecord): Uint8Array {
  if (record.packageId.length !== 16 || record.bundleSha256.length !== 32)
    throw new Error("Control identifiers have invalid lengths");
  const writer = new ByteWriter(CONTROL_RECORD_BYTES);
  writer.data(CONTROL_MAGIC);
  writer.u8(TRANSPORT_VERSION);
  writer.u8(record.profileId);
  writer.u8(0);
  writer.u8(0);
  writer.data(record.packageId);
  writer.u64(record.originalSize);
  writer.u64(record.encodedSize);
  writer.u32(record.dataFrames);
  writer.u8(record.fecSymbols);
  writer.u16(record.dataBytesPerFrame);
  writer.data(record.bundleSha256);
  writer.u32(crc32(writer.bytes.subarray(0, 79)));
  return writer.bytes;
}

export function unpackControl(data: Uint8Array): ControlRecord {
  if (data.length !== CONTROL_RECORD_BYTES)
    throw new Error("Control record has an invalid length");
  const magic = data.subarray(0, 4);
  if (
    (!equalBytes(magic, CONTROL_MAGIC) &&
      !equalBytes(magic, LEGACY_CONTROL_MAGIC)) ||
    data[4] !== TRANSPORT_VERSION ||
    data[5] !== PROFILE_ID ||
    data[6] !== 0 ||
    data[7] !== 0
  )
    throw new Error("Unsupported MP4Modem control record");
  if (crc32(data.subarray(0, 79)) !== readU32(data, 79))
    throw new Error("Control CRC mismatch");
  return {
    profileId: data[5] ?? 0,
    packageId: data.slice(8, 24),
    originalSize: readU64(data, 24),
    encodedSize: readU64(data, 32),
    dataFrames: readU32(data, 40),
    fecSymbols: data[44] ?? 0,
    dataBytesPerFrame: readU16(data, 45),
    bundleSha256: data.slice(47, 79),
  };
}

function controlPayload(record: ControlRecord): Uint8Array {
  const packed = packControl(record);
  const payload = new Uint8Array(FRAME_DATA_BYTES);
  for (let copy = 0; copy < CONTROL_COPIES; copy += 1)
    payload.set(packed, copy * CONTROL_RECORD_BYTES);
  return payload;
}

function decodeControlPayload(data: Uint8Array): ControlRecord {
  const voted = new Uint8Array(CONTROL_RECORD_BYTES);
  for (let byte = 0; byte < CONTROL_RECORD_BYTES; byte += 1) {
    for (let bit = 0; bit < 8; bit += 1) {
      let votes = 0;
      for (let copy = 0; copy < CONTROL_COPIES; copy += 1)
        votes +=
          ((data[copy * CONTROL_RECORD_BYTES + byte] ?? 0) >> (7 - bit)) & 1;
      if (votes >= 9) voted[byte] = (voted[byte] ?? 0) | (1 << (7 - bit));
    }
  }
  return unpackControl(voted);
}

function dataPayload(segment: Uint8Array): Uint8Array {
  const writer = new ByteWriter(FRAME_DATA_BYTES);
  const padded = new Uint8Array(DATA_BYTES_PER_FRAME);
  padded.set(segment);
  writer.data(padded);
  writer.u32(crc32(padded));
  return writer.bytes;
}

function decodeDataPayload(data: Uint8Array): {
  payload: Uint8Array;
  crcValid: boolean;
} {
  const payload = data.slice(0, DATA_BYTES_PER_FRAME);
  return {
    payload,
    crcValid: crc32(payload) === readU32(data, DATA_BYTES_PER_FRAME),
  };
}

function validateControl(control: ControlRecord): void {
  if (control.originalSize <= 0 || control.originalSize > MAXIMUM_BUNDLE_BYTES)
    throw new Error("Bundle size is outside the supported range");
  if (
    control.fecSymbols !== FEC_SYMBOLS ||
    control.dataBytesPerFrame !== DATA_BYTES_PER_FRAME
  )
    throw new Error("Unsupported FEC or frame capacity");
  const expectedBlocks = Math.max(
    1,
    Math.ceil(control.originalSize / FEC_PAYLOAD_SIZE),
  );
  if (control.encodedSize !== expectedBlocks * RS_BLOCK_SIZE)
    throw new Error("Encoded size is inconsistent");
  if (
    control.dataFrames !== Math.ceil(control.encodedSize / DATA_BYTES_PER_FRAME)
  )
    throw new Error("Data frame count is inconsistent");
}

function controlsEqual(left: ControlRecord, right: ControlRecord): boolean {
  return (
    left.profileId === right.profileId &&
    equalBytes(left.packageId, right.packageId) &&
    left.originalSize === right.originalSize &&
    left.encodedSize === right.encodedSize &&
    left.dataFrames === right.dataFrames &&
    left.fecSymbols === right.fecSymbols &&
    left.dataBytesPerFrame === right.dataBytesPerFrame &&
    equalBytes(left.bundleSha256, right.bundleSha256)
  );
}

export async function encodeTransport(
  bundle: Uint8Array,
): Promise<EncodedTransport> {
  if (bundle.length === 0 || bundle.length > MAXIMUM_BUNDLE_BYTES)
    throw new Error("Bundle size is outside the supported range");
  const estimate = estimateTransport(bundle.length, 0, 0);
  const fec = encodeFec(bundle);
  const digest = await sha256(bundle);
  const control: ControlRecord = {
    profileId: PROFILE_ID,
    packageId: digest.slice(0, 16),
    originalSize: bundle.length,
    encodedSize: fec.data.length,
    dataFrames: estimate.frameCount,
    fecSymbols: FEC_SYMBOLS,
    dataBytesPerFrame: DATA_BYTES_PER_FRAME,
    bundleSha256: digest,
  };
  function* frames(): Generator<Uint8Array> {
    let controlIndex = 0;
    for (let dataIndex = 0; dataIndex < estimate.frameCount; dataIndex += 1) {
      if (dataIndex % DATA_FRAMES_PER_SUPERFRAME === 0) {
        const tag: CompactTag = {
          profileId: PROFILE_ID,
          epoch: 0,
          phase: 0,
          flags: CONTROL_FLAG,
          symbolId: controlIndex++,
        };
        yield encodeBasisFrame(tag, controlPayload(control));
      }
      const tag: CompactTag = {
        profileId: PROFILE_ID,
        epoch: 0,
        phase: 0,
        flags: 0,
        symbolId: dataIndex,
      };
      yield encodeBasisFrame(
        tag,
        dataPayload(
          fec.data.subarray(
            dataIndex * DATA_BYTES_PER_FRAME,
            (dataIndex + 1) * DATA_BYTES_PER_FRAME,
          ),
        ),
      );
    }
  }
  return {
    ...estimate,
    videoFrameCount: estimate.frameCount + estimate.controlFrames,
    durationSeconds: (estimate.frameCount + estimate.controlFrames) / FPS,
    bundleBytes: bundle.length,
    bundleSha256: bytesToHex(digest),
    frames: { [Symbol.iterator]: frames },
  };
}

export async function decodeTransportFrames(
  frames: AsyncIterable<Uint8Array> | Iterable<Uint8Array>,
): Promise<DecodedTransport> {
  const controls: ControlRecord[] = [];
  const packets = new Map<number, Uint8Array>();
  const packetValidity = new Map<number, boolean>();
  let decodedVideoFrames = 0;
  let readableTags = 0;
  let crcFailedDataFrames = 0;
  let minimumPilotSeparation: number | null = null;
  for await (const frame of frames) {
    decodedVideoFrames += 1;
    let decoded;
    try {
      decoded = decodeBasisFrame(frame);
    } catch {
      continue;
    }
    readableTags += 1;
    minimumPilotSeparation =
      minimumPilotSeparation === null
        ? decoded.metrics.minimumPilotSeparation
        : Math.min(
            minimumPilotSeparation,
            decoded.metrics.minimumPilotSeparation,
          );
    if ((decoded.tag.flags & CONTROL_FLAG) !== 0) {
      let control: ControlRecord | undefined;
      try {
        control = decodeControlPayload(decoded.data);
        validateControl(control);
      } catch {
        /* An unreadable control copy is ignored. */
      }
      if (control === undefined) continue;
      if (
        controls.length > 0 &&
        !controlsEqual(controls[0] as ControlRecord, control)
      )
        throw new Error("Conflicting control records");
      controls.push(control);
      continue;
    }
    if (decoded.tag.flags !== 0) continue;
    const packet = decodeDataPayload(decoded.data);
    if (!packet.crcValid) crcFailedDataFrames += 1;
    const previous = packetValidity.get(decoded.tag.symbolId);
    if (previous === undefined || (packet.crcValid && !previous)) {
      packets.set(decoded.tag.symbolId, packet.payload);
      packetValidity.set(decoded.tag.symbolId, packet.crcValid);
    }
  }
  const control = controls[0];
  if (!control) throw new Error("No readable MP4Modem control frame was found");
  const encoded = new Uint8Array(control.encodedSize);
  const validPackets = new Map<number, Uint8Array>();
  for (const [index, packet] of packets) {
    if (index < 0 || index >= control.dataFrames) continue;
    validPackets.set(index, packet);
    encoded.set(
      packet.subarray(
        0,
        Math.min(
          DATA_BYTES_PER_FRAME,
          encoded.length - index * DATA_BYTES_PER_FRAME,
        ),
      ),
      index * DATA_BYTES_PER_FRAME,
    );
  }
  const erasures = new Set<number>();
  for (let index = 0; index < control.dataFrames; index += 1) {
    if (validPackets.has(index)) continue;
    for (
      let offset = index * DATA_BYTES_PER_FRAME;
      offset < Math.min((index + 1) * DATA_BYTES_PER_FRAME, encoded.length);
      offset += 1
    )
      erasures.add(offset);
  }
  const bundle = decodeFec(encoded, control.originalSize, erasures);
  const digest = await sha256(bundle);
  if (
    !equalBytes(digest, control.bundleSha256) ||
    !equalBytes(digest.subarray(0, 16), control.packageId)
  )
    throw new Error("Recovered Bundle SHA-256 mismatch");
  return {
    bundle,
    diagnostics: {
      path: "video-v2",
      transportVersion: 2,
      profile: DEFAULT_PROFILE,
      decodedVideoFrames,
      readableTags,
      readableControlFrames: controls.length,
      recoveredDataFrames: validPackets.size,
      crcValidDataFrames: [...validPackets.keys()].filter((index) =>
        packetValidity.get(index),
      ).length,
      expectedDataFrames: control.dataFrames,
      missingDataFrames: control.dataFrames - validPackets.size,
      crcFailedDataFrames,
      erasedBytes: erasures.size,
      minimumPilotSeparation,
      bundleBytes: bundle.length,
      bundleSha256: bytesToHex(digest),
    },
  };
}
