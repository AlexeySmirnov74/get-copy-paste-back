const CRC_TABLE = new Uint32Array(256);

for (let value = 0; value < CRC_TABLE.length; value += 1) {
  let crc = value;
  for (let bit = 0; bit < 8; bit += 1) {
    crc = (crc & 1) !== 0 ? (crc >>> 1) ^ 0xedb88320 : crc >>> 1;
  }
  CRC_TABLE[value] = crc >>> 0;
}

export function crc32(data: Uint8Array): number {
  let crc = 0xffffffff;
  for (const value of data) {
    crc = (crc >>> 8) ^ (CRC_TABLE[(crc ^ value) & 0xff] ?? 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

export async function sha256(data: Uint8Array): Promise<Uint8Array> {
  const copy = Uint8Array.from(data);
  return new Uint8Array(await crypto.subtle.digest("SHA-256", copy.buffer));
}

export function bytesToHex(data: Uint8Array): string {
  return Array.from(data, (value) => value.toString(16).padStart(2, "0")).join(
    "",
  );
}
