const GF_EXP = new Uint8Array(512);
const GF_LOG = new Uint8Array(256);

let fieldValue = 1;
for (let index = 0; index < 255; index += 1) {
  GF_EXP[index] = fieldValue;
  GF_LOG[fieldValue] = index;
  fieldValue <<= 1;
  if ((fieldValue & 0x100) !== 0) fieldValue ^= 0x11d;
}
for (let index = 255; index < GF_EXP.length; index += 1)
  GF_EXP[index] = GF_EXP[index - 255] ?? 0;

function gfMul(left: number, right: number): number {
  if (left === 0 || right === 0) return 0;
  return GF_EXP[(GF_LOG[left] ?? 0) + (GF_LOG[right] ?? 0)] ?? 0;
}

function gfDiv(left: number, right: number): number {
  if (right === 0) throw new Error("Galois field division by zero");
  if (left === 0) return 0;
  return GF_EXP[((GF_LOG[left] ?? 0) + 255 - (GF_LOG[right] ?? 0)) % 255] ?? 0;
}

function gfPow(value: number, power: number): number {
  if (value === 0) return 0;
  const exponent = ((GF_LOG[value] ?? 0) * power) % 255;
  return GF_EXP[(exponent + 255) % 255] ?? 0;
}

function gfInverse(value: number): number {
  if (value === 0) throw new Error("Galois field inverse of zero");
  return GF_EXP[255 - (GF_LOG[value] ?? 0)] ?? 0;
}

function polyScale(poly: readonly number[], scalar: number): number[] {
  return poly.map((value) => gfMul(value, scalar));
}

function polyAdd(left: readonly number[], right: readonly number[]): number[] {
  const result = new Array<number>(Math.max(left.length, right.length)).fill(0);
  const leftOffset = result.length - left.length;
  const rightOffset = result.length - right.length;
  for (let index = 0; index < left.length; index += 1) {
    const position = index + leftOffset;
    result[position] = (result[position] ?? 0) ^ (left[index] ?? 0);
  }
  for (let index = 0; index < right.length; index += 1) {
    const position = index + rightOffset;
    result[position] = (result[position] ?? 0) ^ (right[index] ?? 0);
  }
  return result;
}

function polyMul(left: readonly number[], right: readonly number[]): number[] {
  const result = new Array<number>(left.length + right.length - 1).fill(0);
  for (let leftIndex = 0; leftIndex < left.length; leftIndex += 1) {
    const leftValue = left[leftIndex] ?? 0;
    if (leftValue === 0) continue;
    for (let rightIndex = 0; rightIndex < right.length; rightIndex += 1) {
      const rightValue = right[rightIndex] ?? 0;
      if (rightValue !== 0) {
        const position = leftIndex + rightIndex;
        result[position] =
          (result[position] ?? 0) ^ gfMul(leftValue, rightValue);
      }
    }
  }
  return result;
}

function polyEval(poly: readonly number[], value: number): number {
  let result = poly[0] ?? 0;
  for (let index = 1; index < poly.length; index += 1)
    result = gfMul(result, value) ^ (poly[index] ?? 0);
  return result;
}

function polyRemainder(
  dividend: readonly number[],
  divisor: readonly number[],
): number[] {
  const output = [...dividend];
  for (
    let index = 0;
    index < dividend.length - divisor.length + 1;
    index += 1
  ) {
    const coefficient = output[index] ?? 0;
    if (coefficient === 0) continue;
    for (
      let divisorIndex = 1;
      divisorIndex < divisor.length;
      divisorIndex += 1
    ) {
      const position = index + divisorIndex;
      output[position] =
        (output[position] ?? 0) ^
        gfMul(divisor[divisorIndex] ?? 0, coefficient);
    }
  }
  return output.slice(output.length - (divisor.length - 1));
}

function generatorPolynomial(symbols: number): number[] {
  let result = [1];
  for (let index = 0; index < symbols; index += 1)
    result = polyMul(result, [1, gfPow(2, index)]);
  return result;
}

export function encodeReedSolomon(
  message: Uint8Array,
  paritySymbols: number,
): Uint8Array {
  if (message.length + paritySymbols > 255)
    throw new Error("Reed-Solomon message is too long");
  const generator = generatorPolynomial(paritySymbols);
  const output = new Uint8Array(message.length + paritySymbols);
  output.set(message);
  const generatorLogs = generator.map((value) => GF_LOG[value] ?? 0);
  for (let index = 0; index < message.length; index += 1) {
    const coefficient = output[index] ?? 0;
    if (coefficient === 0) continue;
    const coefficientLog = GF_LOG[coefficient] ?? 0;
    for (let offset = 1; offset < generator.length; offset += 1) {
      const position = index + offset;
      output[position] =
        (output[position] ?? 0) ^
        (GF_EXP[coefficientLog + (generatorLogs[offset] ?? 0)] ?? 0);
    }
  }
  output.set(message, 0);
  return output;
}

function syndromes(message: readonly number[], symbols: number): number[] {
  return [
    0,
    ...Array.from({ length: symbols }, (_, index) =>
      polyEval(message, gfPow(2, index)),
    ),
  ];
}

function forneySyndromes(
  syndrome: readonly number[],
  erasures: readonly number[],
  messageLength: number,
): number[] {
  const result = syndrome.slice(1);
  for (const position of erasures) {
    const value = gfPow(2, messageLength - 1 - position);
    for (let index = 0; index < result.length - 1; index += 1) {
      result[index] =
        gfMul(result[index] ?? 0, value) ^ (result[index + 1] ?? 0);
    }
  }
  return result;
}

function findErrorLocator(
  syndrome: readonly number[],
  symbols: number,
  erasureCount: number,
): number[] {
  let errorLocator = [1];
  let oldLocator = [1];
  const shift = syndrome.length > symbols ? syndrome.length - symbols : 0;
  for (let iteration = 0; iteration < symbols - erasureCount; iteration += 1) {
    const syndromeIndex = iteration + shift;
    let delta = syndrome[syndromeIndex] ?? 0;
    for (let index = 1; index < errorLocator.length; index += 1) {
      delta ^= gfMul(
        errorLocator[errorLocator.length - 1 - index] ?? 0,
        syndrome[syndromeIndex - index] ?? 0,
      );
    }
    oldLocator = [...oldLocator, 0];
    if (delta === 0) continue;
    if (oldLocator.length > errorLocator.length) {
      const replacement = polyScale(oldLocator, delta);
      oldLocator = polyScale(errorLocator, gfInverse(delta));
      errorLocator = replacement;
    }
    errorLocator = polyAdd(errorLocator, polyScale(oldLocator, delta));
  }
  while (errorLocator.length > 1 && errorLocator[0] === 0) errorLocator.shift();
  const errors = errorLocator.length - 1;
  if ((errors - erasureCount) * 2 + erasureCount > symbols)
    throw new Error("Too many Reed-Solomon errors to correct");
  return errorLocator;
}

function findErrors(
  locator: readonly number[],
  messageLength: number,
): number[] {
  const expected = locator.length - 1;
  const positions: number[] = [];
  for (let index = 0; index < messageLength; index += 1) {
    if (polyEval(locator, gfPow(2, index)) === 0)
      positions.push(messageLength - 1 - index);
  }
  if (positions.length !== expected)
    throw new Error("Could not locate Reed-Solomon errors");
  return positions;
}

function errataLocator(coefficientPositions: readonly number[]): number[] {
  let result = [1];
  for (const position of coefficientPositions)
    result = polyMul(result, polyAdd([1], [gfPow(2, position), 0]));
  return result;
}

function errorEvaluator(
  syndrome: readonly number[],
  locator: readonly number[],
  symbols: number,
): number[] {
  return polyRemainder(polyMul(syndrome, locator), [
    1,
    ...new Array<number>(symbols + 1).fill(0),
  ]);
}

function correctErrata(
  message: readonly number[],
  syndrome: readonly number[],
  positions: readonly number[],
): number[] {
  const coefficientPositions = positions.map(
    (position) => message.length - 1 - position,
  );
  const locator = errataLocator(coefficientPositions);
  const evaluator = errorEvaluator(
    [...syndrome].reverse(),
    locator,
    locator.length - 1,
  ).reverse();
  const roots = coefficientPositions.map((position) =>
    gfPow(2, -(255 - position)),
  );
  const result = [...message];
  for (let index = 0; index < roots.length; index += 1) {
    const root = roots[index] ?? 0;
    const inverse = gfInverse(root);
    let derivative = 1;
    for (let other = 0; other < roots.length; other += 1) {
      if (other !== index)
        derivative = gfMul(derivative, 1 ^ gfMul(inverse, roots[other] ?? 0));
    }
    if (derivative === 0)
      throw new Error("Invalid Reed-Solomon error locator derivative");
    const numerator = gfMul(root, polyEval([...evaluator].reverse(), inverse));
    const position = positions[index] ?? 0;
    result[position] = (result[position] ?? 0) ^ gfDiv(numerator, derivative);
  }
  return result;
}

export function decodeReedSolomon(
  codeword: Uint8Array,
  paritySymbols: number,
  erasurePositions: readonly number[] = [],
): Uint8Array {
  if (codeword.length > 255)
    throw new Error("Reed-Solomon codeword is too long");
  if (erasurePositions.length > paritySymbols)
    throw new Error("Too many Reed-Solomon erasures");
  let message = Array.from(codeword);
  for (const position of erasurePositions) {
    if (
      !Number.isInteger(position) ||
      position < 0 ||
      position >= message.length
    )
      throw new Error("Invalid Reed-Solomon erasure position");
    message[position] = 0;
  }
  let syndrome = syndromes(message, paritySymbols);
  if (syndrome.every((value) => value === 0))
    return Uint8Array.from(message.slice(0, -paritySymbols));
  const reduced = forneySyndromes(syndrome, erasurePositions, message.length);
  const locator = findErrorLocator(
    reduced,
    paritySymbols,
    erasurePositions.length,
  );
  const errors = findErrors([...locator].reverse(), message.length);
  const positions = [
    ...erasurePositions,
    ...errors.filter((position) => !erasurePositions.includes(position)),
  ];
  message = correctErrata(message, syndrome, positions);
  syndrome = syndromes(message, paritySymbols);
  if (syndrome.some((value) => value !== 0))
    throw new Error("Could not correct Reed-Solomon message");
  return Uint8Array.from(message.slice(0, -paritySymbols));
}
