import assert from "node:assert/strict";
import { createHash, webcrypto } from "node:crypto";
import { readFile } from "node:fs/promises";
import test from "node:test";

if (!globalThis.crypto) globalThis.crypto = webcrypto;

import {
  FRAME_DATA_BYTES,
  PROFILE_ID,
  createBundle,
  crc32,
  decodeBasisFrame,
  decodeReedSolomon,
  decodeTransportFrames,
  encodeBasisFrame,
  encodeFec,
  encodeReedSolomon,
  encodeTransport,
  extractBundle,
  inspectBundle,
  maximumBundleBytes,
  packTag,
} from "../dist/core/index.js";

const digest = (data) => createHash("sha256").update(data).digest("hex");
const vector = JSON.parse(
  await readFile(
    new URL("../../../test-vectors/transport-v2.json", import.meta.url),
    "utf8",
  ),
);

test("CRC-32 uses the IEEE check value", () => {
  assert.equal(
    crc32(new TextEncoder().encode(vector.crc32.inputUtf8))
      .toString(16)
      .padStart(8, "0"),
    vector.crc32.expectedHex,
  );
});

test("RS(255,191) matches the Python golden codeword and repairs errors", () => {
  const message = Uint8Array.from({ length: 191 }, (_, index) => index);
  const encoded = encodeReedSolomon(message, 64);
  assert.equal(digest(encoded), vector.reedSolomon.expectedCodewordSha256);
  const damaged = encoded.slice();
  const erasures = [];
  for (let index = 0; index < 20; index += 1) {
    damaged[index] ^= 0x55;
    erasures.push(index);
  }
  for (let index = 30; index < 40; index += 1) damaged[index] ^= 0xa3;
  assert.deepEqual(decodeReedSolomon(damaged, 64, erasures), message);
});

test("Payload Bundle bytes match the Python canonical ZIP", async () => {
  const bundle = await createBundle(
    [
      { path: "b.json", data: new TextEncoder().encode('{"ok":true}') },
      { path: "a.txt", data: new TextEncoder().encode("hello") },
    ],
    vector.bundle.title,
    vector.bundle.createdAt,
  );
  assert.equal(bundle.length, vector.bundle.expectedBytes);
  assert.equal(digest(bundle), vector.bundle.expectedSha256);
  const fec = encodeFec(bundle).data;
  assert.equal(fec.length, vector.bundle.expectedFecBytes);
  assert.equal(digest(fec), vector.bundle.expectedFecSha256);
  const manifest = await inspectBundle(bundle);
  assert.deepEqual(
    manifest.files.map((item) => item.path),
    ["files/a.txt", "files/b.json"],
  );
  const extracted = await extractBundle(bundle);
  assert.deepEqual(
    extracted.entries.map((item) => item.path),
    ["files/a.txt", "files/b.json"],
  );
  assert.equal(new TextDecoder().decode(extracted.entries[0].data), "hello");
  extracted.entries[0].data[0] ^= 0xff;
  const extractedAgain = await extractBundle(bundle);
  assert.equal(
    new TextDecoder().decode(extractedAgain.entries[0].data),
    "hello",
  );
});

test("Unicode NFC Bundle bytes match Python", async () => {
  const bundle = await createBundle(
    [{ path: "cafe\u0301.txt", data: new TextEncoder().encode("héllo") }],
    "Cafe\u0301",
  );
  assert.equal(bundle.length, 482);
  assert.equal(
    digest(bundle),
    "09872f498bf54f367a8265bb04fa0660e5c9df9919fb60c3f86f6a391650beb9",
  );
});

test("legacy FPK1 and deflated ZIP entries remain decode-only compatible", async () => {
  const legacy = Buffer.from(
    "RlBLMVBLAwQUAAAACAD4XQldovwJ37wAAADvAAAADQAAAG1hbmlmZXN0Lmpzb24djrtuwzAMRfd+huY2IW09bG/dO2Zq0YG2qEao7Bg2U6QN8u+lDdzp3gPy3M2wMAnHVzGdwbaBF0DNCaDb826eTcqFV9N93M2YR1ZO+CbHuVCedJ1Jztrt0LHwFw2/B7mJLuuZKud1C9ZBwy2GyOg9A6a6odRaqHzNyQ/QU83kYorkEFOfQsVVQNe3npsQ7XYr/+lna+HxqUKXZaRNOC008kzD96G/TrGwgpKlbI5vu4kWP7ys+TKZDh9P/1BLAwQUAAAACAD4XQlddLd9zh0AAAC4AQAAEAAAAGZpbGVzL2xlZ2FjeS50eHTLSU1PTK5USElNy0ksSVUoSKzMyU9MyRkVHeSiAFBLAQIUAxQAAAAIAPhdCV2i/AnfvAAAAO8AAAANAAAAAAAAAAAAAACAAQAAAABtYW5pZmVzdC5qc29uUEsBAhQDFAAAAAgA+F0JXXS3fc4dAAAAuAEAABAAAAAAAAAAAAAAAIAB5wAAAGZpbGVzL2xlZ2FjeS50eHRQSwUGAAAAAAIAAgB5AAAAMgEAAAAA",
    "base64",
  );
  const manifest = await inspectBundle(legacy);
  assert.equal(manifest.format, "framepack.bundle");
  assert.equal(manifest.files[0].path, "files/legacy.txt");
});

test("basis modulation matches the Python frame hash", () => {
  const tag = {
    profileId: PROFILE_ID,
    epoch: vector.tag.epoch,
    phase: vector.tag.phase,
    flags: vector.tag.flags,
    symbolId: vector.tag.symbolId,
  };
  const payload = Uint8Array.from(
    { length: FRAME_DATA_BYTES },
    (_, index) => (index * 37) % 256,
  );
  assert.equal(
    Buffer.from(packTag(tag)).toString("hex"),
    vector.tag.expectedHex,
  );
  const frame = encodeBasisFrame(tag, payload);
  assert.equal(frame.length, vector.basisFrame.expectedBytes);
  assert.equal(digest(frame), vector.basisFrame.expectedSha256);
  const decoded = decodeBasisFrame(frame);
  assert.deepEqual(decoded.tag, tag);
  assert.deepEqual(decoded.data, payload);
});

test("Transport V2 round-trips a canonical Bundle", async () => {
  const bundle = await createBundle(
    [{ path: "hello.txt", data: new TextEncoder().encode("hello browser") }],
    "Browser",
  );
  const encoded = await encodeTransport(bundle);
  const decoded = await decodeTransportFrames(encoded.frames);
  assert.deepEqual(decoded.bundle, bundle);
  assert.equal(decoded.diagnostics.missingDataFrames, 0);
  assert.equal(decoded.diagnostics.crcFailedDataFrames, 0);
});

test("the browser and Python capacity boundaries agree", () => {
  assert.equal(maximumBundleBytes(), 4_252_193);
});
