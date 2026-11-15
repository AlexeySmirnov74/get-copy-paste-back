using System;
using HorizonClient;

internal static class DeliveryReceiptTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            Guid session = Guid.NewGuid(), transfer = Guid.NewGuid();
            foreach (int length in new[] { 1, 10240, 32 * 1024 * 1024 })
            {
                byte[] bytes = DeliveryReceipt.Encode(session, transfer, length);
                DeliveryReceipt receipt;
                True(bytes.Length == 52 && bytes.Length < 256, "A receipt fits a single audio packet.");
                True(DeliveryReceipt.TryDecode(bytes, session, out receipt) && receipt.Session == session && receipt.Transfer == transfer && receipt.EncodedBytes == length,
                    "Receipt retains the session, exact video transfer and encoded payload size.");
                True(!DeliveryReceipt.TryDecode(bytes, Guid.NewGuid(), out receipt) && receipt == null, "Foreign session rejected.");
                for (int offset = 0; offset < bytes.Length; offset++)
                    for (int bit = 0; bit < 8; bit++)
                    {
                        bytes[offset] ^= (byte)(1 << bit);
                        True(!DeliveryReceipt.TryDecode(bytes, session, out receipt) && receipt == null, "Every single-bit corruption is rejected.");
                        bytes[offset] ^= (byte)(1 << bit);
                    }
                for (int size = 0; size < bytes.Length; size++)
                {
                    byte[] truncated = new byte[size]; Array.Copy(bytes, truncated, size);
                    True(!DeliveryReceipt.TryDecode(truncated, session, out receipt), "Truncated control frame rejected.");
                }
                byte[] extra = new byte[bytes.Length + 1]; Array.Copy(bytes, extra, bytes.Length);
                True(!DeliveryReceipt.TryDecode(extra, session, out receipt), "Trailing bytes rejected.");
            }
            DeliveryReceipt invalid;
            True(!DeliveryReceipt.TryDecode(null, session, out invalid) && invalid == null, "Null frame rejected.");
            True(!DeliveryReceipt.TryDecode(DeliveryReceipt.Encode(session, transfer, 5), Guid.Empty, out invalid), "An unset session cannot accept a receipt.");
            Throws(delegate { DeliveryReceipt.Encode(Guid.Empty, transfer, 1); });
            Throws(delegate { DeliveryReceipt.Encode(session, Guid.Empty, 1); });
            Throws(delegate { DeliveryReceipt.Encode(session, transfer, 0); });
            Throws(delegate { DeliveryReceipt.Encode(session, transfer, -1); });
            Throws(delegate { DeliveryReceipt.Encode(session, transfer, 32 * 1024 * 1024 + 1); });
            Console.WriteLine("Delivery receipt checks passed: " + checks + " assertions (session and transfer binding, encoded size boundaries, all single-bit corruptions, truncated and oversized frames). No audio or clipboard access.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Throws(Action action)
    {
        try { action(); } catch (ArgumentException) { checks++; return; }
        throw new Exception("Invalid delivery metadata must not be encoded.");
    }
    private static void True(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
