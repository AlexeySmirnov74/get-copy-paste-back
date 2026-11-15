// C# adaptation of grmchn/mp4modem's packages/browser/src/core/reed-solomon.ts.
// Upstream is distributed under Apache-2.0; see third_party/mp4modem/upstream/LICENSE.
// Changes: bounded .NET arguments, byte-array API, and InvalidDataException for
// uncorrectable codewords. Polynomial and GF(256) conventions match upstream.
using System;
using System.Collections.Generic;
using System.IO;

namespace HorizonClient
{
    internal static class Mp4ModemReedSolomon
    {
        private static readonly byte[] Exp = new byte[512];
        private static readonly byte[] Log = new byte[256];

        static Mp4ModemReedSolomon()
        {
            int value = 1;
            for (int i = 0; i < 255; i++) { Exp[i] = (byte)value; Log[value] = (byte)i; value <<= 1; if ((value & 0x100) != 0) value ^= 0x11d; }
            for (int i = 255; i < Exp.Length; i++) Exp[i] = Exp[i - 255];
        }

        internal static byte[] Encode(byte[] message, int paritySymbols)
        {
            if (message == null) throw new ArgumentNullException("message");
            CheckParity(paritySymbols);
            if (message.Length + paritySymbols > 255) throw new ArgumentException("Reed-Solomon message is too long.", "message");
            int[] generator = Generator(paritySymbols), generatorLogs = new int[paritySymbols + 1];
            for (int i = 0; i < generator.Length; i++) generatorLogs[i] = Log[generator[i]];
            byte[] output = new byte[message.Length + paritySymbols]; Buffer.BlockCopy(message, 0, output, 0, message.Length);
            for (int i = 0; i < message.Length; i++)
            {
                int coefficient = output[i]; if (coefficient == 0) continue; int coefficientLog = Log[coefficient];
                for (int j = 1; j < generator.Length; j++) output[i + j] ^= Exp[coefficientLog + generatorLogs[j]];
            }
            Buffer.BlockCopy(message, 0, output, 0, message.Length); return output;
        }

        internal static byte[] Decode(byte[] codeword, int paritySymbols, int[] erasurePositions = null)
        {
            if (codeword == null) throw new ArgumentNullException("codeword"); CheckParity(paritySymbols);
            if (codeword.Length > 255 || codeword.Length < paritySymbols) throw new ArgumentException("Reed-Solomon codeword length is invalid.", "codeword");
            int[] erasures = erasurePositions == null ? new int[0] : (int[])erasurePositions.Clone();
            if (erasures.Length > paritySymbols) throw new InvalidDataException("Too many Reed-Solomon erasures.");
            int[] message = new int[codeword.Length]; for (int i = 0; i < message.Length; i++) message[i] = codeword[i];
            bool[] erased = new bool[codeword.Length];
            foreach (int position in erasures)
            {
                if (position < 0 || position >= message.Length || erased[position]) throw new ArgumentException("Invalid or repeated Reed-Solomon erasure position.", "erasurePositions");
                erased[position] = true; message[position] = 0;
            }
            int[] syndrome = Syndromes(message, paritySymbols);
            if (AllZero(syndrome)) return MessageBytes(message, paritySymbols);
            int[] reduced = ForneySyndromes(syndrome, erasures, message.Length);
            int[] locator = FindErrorLocator(reduced, paritySymbols, erasures.Length);
            Array.Reverse(locator);
            int[] errors = FindErrors(locator, message.Length);
            var positions = new List<int>(erasures);
            foreach (int position in errors) if (!erased[position]) positions.Add(position);
            message = CorrectErrata(message, syndrome, positions.ToArray());
            if (!AllZero(Syndromes(message, paritySymbols))) throw new InvalidDataException("Could not correct Reed-Solomon message.");
            return MessageBytes(message, paritySymbols);
        }

        private static void CheckParity(int count) { if (count < 1 || count > 255) throw new ArgumentOutOfRangeException("paritySymbols"); }
        private static byte[] MessageBytes(int[] message, int parity)
        {
            byte[] result = new byte[message.Length - parity]; for (int i = 0; i < result.Length; i++) result[i] = (byte)message[i]; return result;
        }
        private static bool AllZero(int[] values) { foreach (int value in values) if (value != 0) return false; return true; }
        private static int Mul(int left, int right) { return left == 0 || right == 0 ? 0 : Exp[Log[left] + Log[right]]; }
        private static int Div(int left, int right)
        {
            if (right == 0) throw new InvalidDataException("Galois field division by zero."); return left == 0 ? 0 : Exp[(Log[left] + 255 - Log[right]) % 255];
        }
        private static int Pow(int value, int power) { if (value == 0) return 0; int exponent = (Log[value] * power) % 255; return Exp[(exponent + 255) % 255]; }
        private static int Inverse(int value) { if (value == 0) throw new InvalidDataException("Galois field inverse of zero."); return Exp[255 - Log[value]]; }
        private static int[] Scale(int[] poly, int scalar) { int[] result = new int[poly.Length]; for (int i = 0; i < poly.Length; i++) result[i] = Mul(poly[i], scalar); return result; }
        private static int[] Add(int[] left, int[] right)
        {
            int[] result = new int[Math.Max(left.Length, right.Length)];
            for (int i = 0; i < left.Length; i++) result[i + result.Length - left.Length] ^= left[i];
            for (int i = 0; i < right.Length; i++) result[i + result.Length - right.Length] ^= right[i]; return result;
        }
        private static int[] Multiply(int[] left, int[] right)
        {
            int[] result = new int[left.Length + right.Length - 1];
            for (int i = 0; i < left.Length; i++) if (left[i] != 0) for (int j = 0; j < right.Length; j++) if (right[j] != 0) result[i + j] ^= Mul(left[i], right[j]); return result;
        }
        private static int Evaluate(int[] poly, int value) { int result = poly.Length == 0 ? 0 : poly[0]; for (int i = 1; i < poly.Length; i++) result = Mul(result, value) ^ poly[i]; return result; }
        private static int[] Slice(int[] source, int offset, int length) { int[] result = new int[length]; Array.Copy(source, offset, result, 0, length); return result; }
        private static int[] Remainder(int[] dividend, int[] divisor)
        {
            int[] output = (int[])dividend.Clone();
            for (int i = 0; i < dividend.Length - divisor.Length + 1; i++)
            {
                int coefficient = output[i]; if (coefficient == 0) continue;
                for (int j = 1; j < divisor.Length; j++) output[i + j] ^= Mul(divisor[j], coefficient);
            }
            return Slice(output, output.Length - divisor.Length + 1, divisor.Length - 1);
        }
        private static int[] Generator(int symbols)
        {
            int[] result = new[] { 1 }; for (int i = 0; i < symbols; i++) result = Multiply(result, new[] { 1, Pow(2, i) }); return result;
        }
        private static int[] Syndromes(int[] message, int symbols)
        {
            int[] result = new int[symbols + 1]; for (int i = 0; i < symbols; i++) result[i + 1] = Evaluate(message, Pow(2, i)); return result;
        }
        private static int[] ForneySyndromes(int[] syndrome, int[] erasures, int messageLength)
        {
            int[] result = Slice(syndrome, 1, syndrome.Length - 1);
            foreach (int position in erasures)
            {
                int value = Pow(2, messageLength - 1 - position);
                for (int i = 0; i < result.Length - 1; i++) result[i] = Mul(result[i], value) ^ result[i + 1];
            }
            return result;
        }
        private static int[] FindErrorLocator(int[] syndrome, int symbols, int erasureCount)
        {
            int[] locator = new[] { 1 }, old = new[] { 1 }; int shift = syndrome.Length > symbols ? syndrome.Length - symbols : 0;
            for (int iteration = 0; iteration < symbols - erasureCount; iteration++)
            {
                int syndromeIndex = iteration + shift, delta = At(syndrome, syndromeIndex);
                for (int i = 1; i < locator.Length; i++) delta ^= Mul(locator[locator.Length - 1 - i], At(syndrome, syndromeIndex - i));
                Array.Resize(ref old, old.Length + 1);
                if (delta == 0) continue;
                if (old.Length > locator.Length) { int[] replacement = Scale(old, delta); old = Scale(locator, Inverse(delta)); locator = replacement; }
                locator = Add(locator, Scale(old, delta));
            }
            int first = 0; while (first < locator.Length - 1 && locator[first] == 0) first++;
            if (first != 0) locator = Slice(locator, first, locator.Length - first);
            int errors = locator.Length - 1;
            if ((errors - erasureCount) * 2 + erasureCount > symbols) throw new InvalidDataException("Too many Reed-Solomon errors to correct.");
            return locator;
        }
        private static int At(int[] values, int index) { return index < 0 || index >= values.Length ? 0 : values[index]; }
        private static int[] FindErrors(int[] locator, int messageLength)
        {
            var result = new List<int>(); for (int i = 0; i < messageLength; i++) if (Evaluate(locator, Pow(2, i)) == 0) result.Add(messageLength - 1 - i);
            if (result.Count != locator.Length - 1) throw new InvalidDataException("Could not locate Reed-Solomon errors."); return result.ToArray();
        }
        private static int[] ErrataLocator(int[] positions)
        {
            int[] result = new[] { 1 }; foreach (int position in positions) result = Multiply(result, Add(new[] { 1 }, new[] { Pow(2, position), 0 })); return result;
        }
        private static int[] ErrorEvaluator(int[] syndrome, int[] locator, int symbols)
        {
            int[] divisor = new int[symbols + 2]; divisor[0] = 1; return Remainder(Multiply(syndrome, locator), divisor);
        }
        private static int[] CorrectErrata(int[] message, int[] syndrome, int[] positions)
        {
            int[] coefficients = new int[positions.Length]; for (int i = 0; i < coefficients.Length; i++) coefficients[i] = message.Length - 1 - positions[i];
            int[] locator = ErrataLocator(coefficients), reversedSyndrome = (int[])syndrome.Clone(); Array.Reverse(reversedSyndrome);
            int[] evaluator = ErrorEvaluator(reversedSyndrome, locator, locator.Length - 1); Array.Reverse(evaluator);
            int[] roots = new int[coefficients.Length]; for (int i = 0; i < roots.Length; i++) roots[i] = Pow(2, -(255 - coefficients[i]));
            int[] result = (int[])message.Clone();
            for (int i = 0; i < roots.Length; i++)
            {
                int root = roots[i], inverse = Inverse(root), derivative = 1;
                for (int j = 0; j < roots.Length; j++) if (j != i) derivative = Mul(derivative, 1 ^ Mul(inverse, roots[j]));
                if (derivative == 0) throw new InvalidDataException("Invalid Reed-Solomon error locator derivative.");
                int[] reversed = (int[])evaluator.Clone(); Array.Reverse(reversed);
                int numerator = Mul(root, Evaluate(reversed, inverse)); result[positions[i]] ^= Div(numerator, derivative);
            }
            return result;
        }
    }
}
