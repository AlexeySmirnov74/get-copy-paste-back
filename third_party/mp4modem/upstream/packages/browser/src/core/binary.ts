const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder("utf-8", { fatal: true });

export function concatBytes(...parts: readonly Uint8Array[]): Uint8Array {
  const result = new Uint8Array(
    parts.reduce((sum, part) => sum + part.length, 0),
  );
  let offset = 0;
  for (const part of parts) {
    result.set(part, offset);
    offset += part.length;
  }
  return result;
}

export function utf8(value: string): Uint8Array {
  return textEncoder.encode(value);
}

export function decodeUtf8(value: Uint8Array): string {
  return textDecoder.decode(value);
}

export function equalBytes(left: Uint8Array, right: Uint8Array): boolean {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index += 1) {
    difference |= (left[index] ?? 0) ^ (right[index] ?? 0);
  }
  return difference === 0;
}

export class ByteWriter {
  readonly bytes: Uint8Array;
  readonly view: DataView;
  offset = 0;

  constructor(length: number) {
    this.bytes = new Uint8Array(length);
    this.view = new DataView(this.bytes.buffer);
  }

  u8(value: number): void {
    this.view.setUint8(this.offset, value);
    this.offset += 1;
  }

  u16(value: number): void {
    this.view.setUint16(this.offset, value, false);
    this.offset += 2;
  }

  u32(value: number): void {
    this.view.setUint32(this.offset, value >>> 0, false);
    this.offset += 4;
  }

  u16le(value: number): void {
    this.view.setUint16(this.offset, value, true);
    this.offset += 2;
  }

  u32le(value: number): void {
    this.view.setUint32(this.offset, value >>> 0, true);
    this.offset += 4;
  }

  u64(value: number): void {
    if (!Number.isSafeInteger(value) || value < 0)
      throw new RangeError("Invalid u64 value");
    this.view.setBigUint64(this.offset, BigInt(value), false);
    this.offset += 8;
  }

  data(value: Uint8Array): void {
    this.bytes.set(value, this.offset);
    this.offset += value.length;
  }
}

export function readU16(
  data: Uint8Array,
  offset: number,
  littleEndian = false,
): number {
  return new DataView(data.buffer, data.byteOffset, data.byteLength).getUint16(
    offset,
    littleEndian,
  );
}

export function readU32(
  data: Uint8Array,
  offset: number,
  littleEndian = false,
): number {
  return new DataView(data.buffer, data.byteOffset, data.byteLength).getUint32(
    offset,
    littleEndian,
  );
}

export function readU64(data: Uint8Array, offset: number): number {
  const value = new DataView(
    data.buffer,
    data.byteOffset,
    data.byteLength,
  ).getBigUint64(offset, false);
  const numberValue = Number(value);
  if (!Number.isSafeInteger(numberValue))
    throw new RangeError("u64 exceeds JavaScript safe integer range");
  return numberValue;
}
