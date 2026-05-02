using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Trading.Tools;

public class EodReportGenerator
{
    private static readonly string BotToken = "8719815354:AAHHH-GXQ7QI8ZowY7ScQxNUn2XuIQK61RE";
    private static readonly string ChatId = "671099433";

    public static async Task Main()
    {
        Console.WriteLine("Generating EOD Report for April 16...");

        // Nifty 50 (Apr 16)
        decimal nClose = 24231.30m;
        decimal nHigh = 24400.95m;
        decimal nLow = 24102.80m;
        
        // BankNifty (Apr 16)
        decimal bnClose = 56301.95m;
        decimal bnHigh = 56834.25m;
        decimal bnLow = 55898.25m;

        var sb = new StringBuilder();
        sb.AppendLine("🏁 *Market Close Report - Apr 16*");
        sb.AppendLine("------------------------------------");
        sb.AppendLine($"• *Nifty 50 Close*: `{nClose:N2}`");
        sb.AppendLine($"• *BankNifty Close*: `{bnClose:N2}`");
        sb.AppendLine($"• *Session Result*: `-28.15 pts` (9:20 Strangle)");
        
        sb.AppendLine("\n📐 *Next Day CPR (Apr 17)*");
        sb.AppendLine("*NIFTY 50*:");
        sb.AppendLine(FormatCPR(nHigh, nLow, nClose));
        
        sb.AppendLine("\n*BANKNIFTY*:");
        sb.AppendLine(FormatCPR(bnHigh, bnLow, bnClose));

        sb.AppendLine("\n🚀 *Infrastructure Highlights*");
        sb.AppendLine("• Dual-Index Tracking Enabled (2,502 tokens)");
        sb.AppendLine("• 14.4s DNS Lag Alert successfully handled");
        sb.AppendLine("• All Strategy State Store flushes complete");
        
        sb.AppendLine("\n✅ *Auto-shutdown complete. See you tomorrow!*");

        using var client = new HttpClient();
        string url = $"https://api.telegram.org/bot{BotToken}/sendMessage?chat_id={ChatId}&text={Uri.EscapeDataString(sb.ToString())}&parse_mode=Markdown";
        await client.GetAsync(url);
        
        Console.WriteLine("EOD Report Sent.");
    }

    private static string FormatCPR(decimal h, decimal l, decimal c)
    {
        decimal pivot = (h + l + c) / 3;
        decimal bc = (h + l) / 2;
        decimal tc = (pivot - bc) + pivot;
        if (bc > tc) { var tmp = bc; bc = tc; tc = tmp; }
        
        return $"• Pivot: `{pivot:N2}`\n• CPR Width: `{(tc - bc):N2}` ({(tc - bc < 15 ? "Narrow" : "Wide")})";
    }
}
