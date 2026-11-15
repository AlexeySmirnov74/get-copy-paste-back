import { ByteWriter, equalBytes, readU32, utf8 } from "./binary.js";
import { crc32 } from "./checksum.js";

export const WIDTH = 1280;
export const HEIGHT = 720;
export const TILE_SIZE = 16;
export const COLUMNS = WIDTH / TILE_SIZE;
export const ROWS = HEIGHT / TILE_SIZE;
export const BLOCK_COUNT = COLUMNS * ROWS;
export const BITS_PER_BLOCK = 4;
export const PROFILE_ID = 0x81;
export const TRANSPORT_VERSION = 2;
export const TAG_BYTES = 16;
export const TAG_COPIES = 3;
export const HEADER_BLOCKS_PER_COPY = TAG_BYTES * 2;
export const PILOT_BLOCK_COUNT = 192;
export const FRAME_DATA_BYTES = 1656;

const TAG_MAGIC = utf8("M2");
const LEGACY_TAG_MAGIC = utf8("F2");
const HEADER_COPY_STARTS = [
  0,
  BLOCK_COUNT / 2 - HEADER_BLOCKS_PER_COPY / 2,
  BLOCK_COUNT - 32,
];
export const HEADER_INDICES = Uint16Array.from(
  HEADER_COPY_STARTS.flatMap((start) =>
    Array.from({ length: HEADER_BLOCKS_PER_COPY }, (_, index) => start + index),
  ),
);
const headerSet = new Set(HEADER_INDICES);
const pilotEligible = Array.from(
  { length: BLOCK_COUNT },
  (_, index) => index,
).filter((index) => !headerSet.has(index));
export const PILOT_INDICES = Uint16Array.from(
  Array.from(
    { length: PILOT_BLOCK_COUNT },
    (_, index) =>
      pilotEligible[
        Math.floor(((index + 0.5) * pilotEligible.length) / PILOT_BLOCK_COUNT)
      ] ?? 0,
  ),
);
const reserved = new Set([...HEADER_INDICES, ...PILOT_INDICES]);
export const DATA_INDICES = Uint16Array.from(
  Array.from({ length: BLOCK_COUNT }, (_, index) => index).filter(
    (index) => !reserved.has(index),
  ),
);

export interface CompactTag {
  profileId: number;
  epoch: number;
  phase: number;
  flags: number;
  symbolId: number;
}

export interface DemodulationMetrics {
  pilotSeparations: [number, number, number, number];
  pilotThresholds: [number, number, number, number];
  minimumPilotSeparation: number;
}

export interface DecodedBasisFrame {
  tag: CompactTag;
  data: Uint8Array;
  metrics: DemodulationMetrics;
}

function bit(data: Uint8Array, index: number): number {
  return ((data[Math.floor(index / 8)] ?? 0) >> (7 - (index % 8))) & 1;
}

function setNibble(
  target: Uint8Array,
  blockIndex: number,
  value: number,
): void {
  const byteIndex = Math.floor(blockIndex / 2);
  if (blockIndex % 2 === 0) target[byteIndex] = (value & 0x0f) << 4;
  else target[byteIndex] = (target[byteIndex] ?? 0) | (value & 0x0f);
}

function getNibble(data: Uint8Array, blockIndex: number): number {
  const value = data[Math.floor(blockIndex / 2)] ?? 0;
  return blockIndex % 2 === 0 ? value >> 4 : value & 0x0f;
}

export function packTag(tag: CompactTag): Uint8Array {
  const writer = new ByteWriter(TAG_BYTES);
  writer.data(TAG_MAGIC);
  writer.u8(TRANSPORT_VERSION);
  writer.u8(tag.profileId);
  writer.u16(tag.epoch);
  writer.u8(tag.phase);
  writer.u8(tag.flags);
  writer.u32(tag.symbolId);
  writer.u32(crc32(writer.bytes.subarray(0, 12)));
  return writer.bytes;
}

export function unpackTag(data: Uint8Array): CompactTag {
  if (data.length !== TAG_BYTES)
    throw new Error("Compact tag has an invalid length");
  const magic = data.subarray(0, 2);
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  if (
    (!equalBytes(magic, TAG_MAGIC) && !equalBytes(magic, LEGACY_TAG_MAGIC)) ||
    data[2] !== TRANSPORT_VERSION ||
    data[3] !== PROFILE_ID
  )
    throw new Error("Unsupported MP4Modem tag");
  if (crc32(data.subarray(0, 12)) !== readU32(data, 12))
    throw new Error("Compact tag CRC mismatch");
  return {
    profileId: data[3] ?? 0,
    epoch: view.getUint16(4, false),
    phase: data[6] ?? 0,
    flags: data[7] ?? 0,
    symbolId: view.getUint32(8, false),
  };
}

function frameNibbles(tag: CompactTag, data: Uint8Array): Uint8Array {
  if (data.length > FRAME_DATA_BYTES)
    throw new Error(`Frame data exceeds ${FRAME_DATA_BYTES} bytes`);
  const padded = new Uint8Array(FRAME_DATA_BYTES);
  padded.set(data);
  const nibbles = new Uint8Array(BLOCK_COUNT);
  const packedTag = packTag(tag);
  for (let copy = 0; copy < TAG_COPIES; copy += 1) {
    for (let index = 0; index < HEADER_BLOCKS_PER_COPY; index += 1)
      nibbles[HEADER_INDICES[copy * HEADER_BLOCKS_PER_COPY + index] ?? 0] =
        getNibble(packedTag, index);
  }
  for (let index = 0; index < PILOT_INDICES.length; index += 1)
    nibbles[PILOT_INDICES[index] ?? 0] = index % 16;
  for (let index = 0; index < DATA_INDICES.length; index += 1)
    nibbles[DATA_INDICES[index] ?? 0] = getNibble(padded, index);
  return nibbles;
}

export function encodeBasisFrame(
  tag: CompactTag,
  data: Uint8Array,
): Uint8Array {
  const nibbles = frameNibbles(tag, data);
  const frame = new Uint8Array((WIDTH * HEIGHT * 3) / 2);
  for (let block = 0; block < BLOCK_COUNT; block += 1) {
    const value = nibbles[block] ?? 0;
    const signs = [3, 2, 1, 0].map((shift) => ((value >> shift) & 1) * 2 - 1);
    const blockRow = Math.floor(block / COLUMNS);
    const blockColumn = block % COLUMNS;
    for (let y = 0; y < TILE_SIZE; y += 1) {
      const vertical = y < TILE_SIZE / 2 ? -1 : 1;
      const rowOffset =
        (blockRow * TILE_SIZE + y) * WIDTH + blockColumn * TILE_SIZE;
      for (let x = 0; x < TILE_SIZE; x += 1) {
        const horizontal = x < TILE_SIZE / 2 ? -1 : 1;
        const luminance =
          128 +
          (signs[0] ?? 0) * 40 +
          (signs[1] ?? 0) * 24 * horizontal +
          (signs[2] ?? 0) * 24 * vertical +
          (signs[3] ?? 0) * 24 * horizontal * vertical;
        frame[rowOffset + x] = Math.max(16, Math.min(240, luminance));
      }
    }
  }
  frame.fill(128, WIDTH * HEIGHT);
  return frame;
}

export function decodeBasisFrame(frame: Uint8Array): DecodedBasisFrame {
  if (frame.length !== (WIDTH * HEIGHT * 3) / 2)
    throw new Error("Unexpected I420 frame size");
  const observations = Array.from({ length: BLOCK_COUNT }, () => [0, 0, 0, 0]);
  for (let block = 0; block < BLOCK_COUNT; block += 1) {
    const blockRow = Math.floor(block / COLUMNS);
    const blockColumn = block % COLUMNS;
    const sums = [0, 0, 0, 0];
    for (let y = 0; y < TILE_SIZE; y += 1) {
      const vertical = y < TILE_SIZE / 2 ? -1 : 1;
      const rowOffset =
        (blockRow * TILE_SIZE + y) * WIDTH + blockColumn * TILE_SIZE;
      for (let x = 0; x < TILE_SIZE; x += 1) {
        const horizontal = x < TILE_SIZE / 2 ? -1 : 1;
        const luminance = frame[rowOffset + x] ?? 0;
        sums[0] = (sums[0] ?? 0) + luminance - 128;
        sums[1] = (sums[1] ?? 0) + luminance * horizontal;
        sums[2] = (sums[2] ?? 0) + luminance * vertical;
        sums[3] = (sums[3] ?? 0) + luminance * horizontal * vertical;
      }
    }
    observations[block] = sums.map((sum) => sum / (TILE_SIZE * TILE_SIZE));
  }
  const thresholds = [0, 0, 0, 0];
  const separations = [0, 0, 0, 0];
  for (let basis = 0; basis < BITS_PER_BLOCK; basis += 1) {
    let low = 0;
    let high = 0;
    let lowCount = 0;
    let highCount = 0;
    for (let index = 0; index < PILOT_INDICES.length; index += 1) {
      const expected = ((index % 16) >> (3 - basis)) & 1;
      const observation = observations[PILOT_INDICES[index] ?? 0]?.[basis] ?? 0;
      if (expected === 0) {
        low += observation;
        lowCount += 1;
      } else {
        high += observation;
        highCount += 1;
      }
    }
    low /= lowCount;
    high /= highCount;
    thresholds[basis] = (low + high) / 2;
    separations[basis] = Math.abs(high - low);
  }
  const decodedNibbles = new Uint8Array(BLOCK_COUNT);
  for (let block = 0; block < BLOCK_COUNT; block += 1) {
    let value = 0;
    for (let basis = 0; basis < BITS_PER_BLOCK; basis += 1) {
      value =
        (value << 1) |
        ((observations[block]?.[basis] ?? 0) >= (thresholds[basis] ?? 0)
          ? 1
          : 0);
    }
    decodedNibbles[block] = value;
  }
  const votedTag = new Uint8Array(TAG_BYTES);
  for (let bitIndex = 0; bitIndex < TAG_BYTES * 8; bitIndex += 1) {
    let votes = 0;
    for (let copy = 0; copy < TAG_COPIES; copy += 1) {
      const nibbleIndex = Math.floor(bitIndex / 4);
      const nibble =
        decodedNibbles[
          HEADER_INDICES[copy * HEADER_BLOCKS_PER_COPY + nibbleIndex] ?? 0
        ] ?? 0;
      votes += (nibble >> (3 - (bitIndex % 4))) & 1;
    }
    if (votes >= 2)
      votedTag[Math.floor(bitIndex / 8)] =
        (votedTag[Math.floor(bitIndex / 8)] ?? 0) | (1 << (7 - (bitIndex % 8)));
  }
  const payload = new Uint8Array(FRAME_DATA_BYTES);
  for (let index = 0; index < DATA_INDICES.length; index += 1)
    setNibble(payload, index, decodedNibbles[DATA_INDICES[index] ?? 0] ?? 0);
  const metrics = {
    pilotSeparations: separations as [number, number, number, number],
    pilotThresholds: thresholds as [number, number, number, number],
    minimumPilotSeparation: Math.min(...separations),
  };
  return { tag: unpackTag(votedTag), data: payload, metrics };
}
