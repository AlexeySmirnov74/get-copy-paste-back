import { ByteWriter, concatBytes, readU32 } from "./binary.js";
import { crc32 } from "./checksum.js";
import { decodeReedSolomon, encodeReedSolomon } from "./reed-solomon.js";

export const RS_BLOCK_SIZE = 255;
export const FEC_SYMBOLS = 64;
export const RS_DATA_SIZE = RS_BLOCK_SIZE - FEC_SYMBOLS;
export const FEC_PAYLOAD_SIZE = RS_DATA_SIZE - 4;

export interface FecEncoded {
  data: Uint8Array;
  blockCount: number;
}

export function interleave(blocks: readonly Uint8Array[]): Uint8Array {
  if (blocks.length === 0) return new Uint8Array();
  const width = blocks[0]?.length ?? 0;
  if (blocks.some((block) => block.length !== width))
    throw new Error("Interleave block width mismatch");
  const result = new Uint8Array(blocks.length * width);
  let offset = 0;
  for (let column = 0; column < width; column += 1) {
    for (const block of blocks) result[offset++] = block[column] ?? 0;
  }
  return result;
}

export function deinterleave(
  data: Uint8Array,
  blockCount: number,
): Uint8Array[] {
  if (data.length !== blockCount * RS_BLOCK_SIZE)
    throw new Error("Interleaved data length mismatch");
  const blocks = Array.from(
    { length: blockCount },
    () => new Uint8Array(RS_BLOCK_SIZE),
  );
  let offset = 0;
  for (let column = 0; column < RS_BLOCK_SIZE; column += 1) {
    for (const block of blocks) block[column] = data[offset++] ?? 0;
  }
  return blocks;
}

export function encodeFec(data: Uint8Array): FecEncoded {
  const blockCount = Math.max(1, Math.ceil(data.length / FEC_PAYLOAD_SIZE));
  const blocks: Uint8Array[] = [];
  for (let index = 0; index < blockCount; index += 1) {
    const chunk = new Uint8Array(FEC_PAYLOAD_SIZE);
    chunk.set(
      data.subarray(index * FEC_PAYLOAD_SIZE, (index + 1) * FEC_PAYLOAD_SIZE),
    );
    const protectedData = new ByteWriter(RS_DATA_SIZE);
    protectedData.data(chunk);
    protectedData.u32(crc32(chunk));
    blocks.push(encodeReedSolomon(protectedData.bytes, FEC_SYMBOLS));
  }
  return { data: interleave(blocks), blockCount };
}

export function decodeFec(
  data: Uint8Array,
  originalSize: number,
  erasures: ReadonlySet<number> = new Set(),
): Uint8Array {
  if (data.length % RS_BLOCK_SIZE !== 0)
    throw new Error("Encoded FEC stream has an invalid length");
  const blockCount = data.length / RS_BLOCK_SIZE;
  const blocks = deinterleave(data, blockCount);
  const decoded: Uint8Array[] = [];
  for (let blockIndex = 0; blockIndex < blocks.length; blockIndex += 1) {
    const positions: number[] = [];
    for (let column = 0; column < RS_BLOCK_SIZE; column += 1) {
      if (erasures.has(column * blockCount + blockIndex))
        positions.push(column);
    }
    const protectedData = decodeReedSolomon(
      blocks[blockIndex] ?? new Uint8Array(),
      FEC_SYMBOLS,
      positions,
    );
    const chunk = protectedData.subarray(0, FEC_PAYLOAD_SIZE);
    if (crc32(chunk) !== readU32(protectedData, FEC_PAYLOAD_SIZE))
      throw new Error(`FEC block ${blockIndex} CRC mismatch`);
    decoded.push(chunk);
  }
  return concatBytes(...decoded).slice(0, originalSize);
}
