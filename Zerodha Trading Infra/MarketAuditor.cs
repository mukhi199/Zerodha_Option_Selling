using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using KiteConnect;

namespace Trading.Tools;

public class MarketAuditor
{
    public static async Task Main()
    {
        string apiKey = "6zfzn8a4x91snnpz";
        string accessToken = "QukMbp13jXznmJhzv1YHs4GNbX6Pe3wO";

        var kite = new Kite(apiKey, accessToken);
        try
        {
            var symbols = new string[] { 
                "NSE:NIFTY 50", 
                "NSE:NIFTY BANK",
                "NFO:NIFTY2642124550CE",
                "NFO:NIFTY2642124150PE",
                "NFO:NIFTY2642124850CE",
                "NFO:NIFTY2642123850PE"
            };

            var quotes = kite.GetQuote(symbols);
            
            Console.WriteLine("--- MARKET STATUS ---");
            Console.WriteLine($"NIFTY 50: {quotes["NSE:NIFTY 50"].LastPrice}");
            Console.WriteLine($"NIFTY BANK: {quotes["NSE:NIFTY BANK"].LastPrice}");
            
            Console.WriteLine("\n--- STRANGLE MTM ---");
            decimal entryCe = 98.55m;
            decimal entryPe = 117.20m;
            decimal currCe = quotes["NFO:NIFTY2642124550CE"].LastPrice;
            decimal currPe = quotes["NFO:NIFTY2642124150PE"].LastPrice;
            
            decimal ceDiff = entryCe - currCe;
            decimal peDiff = entryPe - currPe;
            decimal totalPoints = ceDiff + peDiff;
            decimal mtm = totalPoints * 65; // Lot size 75? Wait, strategy said 65. I'll use 75 for Nifty. 
            // Wait, strategy log said 65.
            
            Console.WriteLine($"CE Short: Entry {entryCe} | Curr {currCe} | P/L: {ceDiff:F1}");
            Console.WriteLine($"PE Short: Entry {entryPe} | Curr {currPe} | P/L: {peDiff:F1}");
            Console.WriteLine($"TOTAL POINTS: {totalPoints:F1}");
            Console.WriteLine($"ESTIMATED MTM (65 qty): {mtm:F0}");
            
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
        }
    }
}
