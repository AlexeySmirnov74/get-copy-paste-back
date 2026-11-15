using System;

namespace HorizonClient
{
    // Pure single-transfer controller. The caller serializes access with the
    // sender's state lock. A newer local copy invalidates an old completion.
    internal sealed class ForwardDeliveryState
    {
        private Guid session, transfer;
        private int bytes, generation;
        private uint clipboardSequence;
        private bool confirmed;
        private byte[] fastToken;

        internal void Expect(Guid session, Guid transfer, int bytes, int generation, uint clipboardSequence)
        {
            if (session == Guid.Empty || transfer == Guid.Empty || bytes < 1 || bytes > VisualProtocol.MaxTransferBytes) throw new ArgumentException("Invalid pending video delivery.");
            this.session = session; this.transfer = transfer; this.bytes = bytes; this.generation = generation;
            this.clipboardSequence = clipboardSequence; confirmed = false;
            fastToken = DeliveryReceipt.FastToken(session, transfer, bytes);
        }
        internal bool Confirm(DeliveryReceipt receipt, int currentGeneration, uint currentClipboardSequence)
        {
            if (receipt == null || confirmed || transfer == Guid.Empty || receipt.Session != session || receipt.Transfer != transfer || receipt.EncodedBytes != bytes ||
                generation != currentGeneration || clipboardSequence != currentClipboardSequence) return false;
            confirmed = true; return true;
        }

        internal bool ConfirmFast(byte[] token, int currentGeneration, uint currentClipboardSequence, out DeliveryReceipt receipt)
        {
            receipt = null;
            if (confirmed || transfer == Guid.Empty || generation != currentGeneration || clipboardSequence != currentClipboardSequence ||
                !DeliveryReceipt.SameToken(token, fastToken)) return false;
            confirmed = true;
            receipt = new DeliveryReceipt { Session = session, Transfer = transfer, EncodedBytes = bytes };
            return true;
        }
    }
}
