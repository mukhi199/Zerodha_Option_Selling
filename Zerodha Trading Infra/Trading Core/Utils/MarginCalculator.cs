using System;
using System.Text;

namespace Trading.Core.Utils
{
    public static class MarginCalculator
    {
        // Brokerage charges (Zerodha standard limits)
        private const decimal BrokeragePerOrder = 20m;

        public static void PrintMarginAndCharges(string symbol, decimal entryPrice, int quantity, bool isLong)
        {
            // Future Contract value
            decimal futureValue = entryPrice * quantity;

            // Normally, margin is ~10-12% of contract value depending on VIX.
            // With Hedging (Buying an OTM option ~5% away), margin drops to ~3-4% of contract value.
            decimal nakedMargin = futureValue * 0.11m;
            decimal hedgedMargin = futureValue * 0.035m; // Rough NSE margin benefit

            // Charges Breakdown for a round trip (Buy + Sell) + Hedge (Buy + Sell Option)
            // Option details
            decimal optionPremiumValue = 10m * quantity; // Assuming we buy a ₹10 OTM option

            // 1. Brokerage (Max ₹20 per executed order. 2 legs for entry, 2 legs for exit)
            decimal totalBrokerage = 4 * BrokeragePerOrder;

            // 2. STT/CTT (0.0125% on Futures Sell Side, 0.0625% on Options Sell Side)
            decimal sttFutures = futureValue * 0.000125m;
            decimal sttOptions = optionPremiumValue * 0.000625m;
            decimal totalStt = sttFutures + sttOptions;

            // 3. Exchange Transaction Charges (NSE Futures = 0.0019%, Options = 0.05% on Premium)
            decimal txnFutures = futureValue * 2 * 0.000019m; // Round trip
            decimal txnOptions = optionPremiumValue * 2 * 0.0005m;
            decimal totalTxn = txnFutures + txnOptions;

            // 4. SEBI Charges (₹10 / crore)
            decimal totalTurnover = (futureValue * 2) + (optionPremiumValue * 2);
            decimal sebiCharges = totalTurnover * 0.000001m;

            // 5. Stamp Duty (0.002% on Futures Buy Side, 0.003% on Options Buy Side)
            decimal stampFutures = futureValue * 0.00002m;
            decimal stampOptions = optionPremiumValue * 0.00003m;
            decimal totalStamp = stampFutures + stampOptions;

            // GST (18% on Brokerage + SEBI + Transaction Charges)
            decimal totalGst = (totalBrokerage + sebiCharges + totalTxn) * 0.18m;

            decimal totalCharges = totalBrokerage + totalStt + totalTxn + sebiCharges + totalStamp + totalGst;
            
            // Output summary
            var sb = new StringBuilder();
            sb.AppendLine($"\n╔════════════════════════════════════════════════════════════╗");
            sb.AppendLine($"║   MARGIN & BROKERAGE CALCULATION ({symbol} 1 LOT)            ║");
            sb.AppendLine($"╚════════════════════════════════════════════════════════════╝");
            sb.AppendLine($"Contract Value:   ₹ {futureValue:N2} (Qty: {quantity} @ {entryPrice:N2})");
            sb.AppendLine($"Naked Margin Required:  ~₹ {nakedMargin:N2} (11%)");
            sb.AppendLine($"Hedged Margin Required: ~₹ {hedgedMargin:N2} (3.5%)  <-- 68% REDUCTION");
            sb.AppendLine($"\n--- Estimated Charges for Complete Round Trip (Hedged Basket) ---");
            sb.AppendLine($"Brokerage:        ₹ {totalBrokerage:N2}");
            sb.AppendLine($"STT/CTT:          ₹ {totalStt:N2}");
            sb.AppendLine($"Exchange Txn:     ₹ {totalTxn:N2}");
            sb.AppendLine($"SEBI Charges:     ₹ {sebiCharges:N2}");
            sb.AppendLine($"Stamp Duty:       ₹ {totalStamp:N2}");
            sb.AppendLine($"GST (18%):        ₹ {totalGst:N2}");
            sb.AppendLine($"────────────────────────────────────────────────────────────");
            sb.AppendLine($"Total Points needed to Breakeven = {(totalCharges / quantity):N2} points");
            sb.AppendLine($"Total Estimated Drag: ₹ {totalCharges:N0}");

            Console.WriteLine(sb.ToString());
        }
    }
}
