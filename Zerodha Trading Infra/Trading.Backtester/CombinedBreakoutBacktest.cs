using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Trading.Core.Models;
using Trading.Core.Utils;
using Trading.Strategy.Services;
using ClosedXML.Excel;

namespace Trading.Backtester
{
    /// <summary>
    /// Combined PDLH + 3-Day Breakout Strategy
    /// - Watches both the Previous Day Last-Hour band AND the 3-Day rolling High/Low simultaneously
    /// - Whichever signal triggers first on a given day gets executed (max 1 trade/day)
    /// - Only Marubozu + Engulfing patterns accepted (highest conviction)
    /// - Fixed SL: 60 pts (Nifty) / 150 pts (BankNifty)
    /// - EOD square-off at 15:15
    /// </summary>
    public static class CombinedBreakoutBacktest
    {
        // ── Pattern whitelist ────────────────────────────────────────────────
        private static readonly HashSet<CandlePatternDetector.CandlePattern> AllowedPatterns = new()
        {
            CandlePatternDetector.CandlePattern.BullishMarubozu,
            CandlePatternDetector.CandlePattern.BearishMarubozu,
            CandlePatternDetector.CandlePattern.BullishEngulfing,
            CandlePatternDetector.CandlePattern.BearishEngulfing,
        };

        const decimal MinBandWidthNifty     = 60m;
        const decimal MinBandWidthBankNifty = 120m;

        public static void Run()
        {
            Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║   COMBINED (PDLH + 3-DAY) BREAKOUT BACKTEST         ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝\n");

            var root        = "/Users/Lenovo/Projects/Zerodha_Option_Selling";
            var niftyFile   = Path.Combine(root, "NIFTY 50_5minute.csv");
            var bankFile    = Path.Combine(root, "NIFTY BANK_5minute.csv");
            var exportPath  = Path.Combine(root, "Backtest_Results_Combined.xlsx");

            var niftyTrades = RunSymbol("NIFTY 50",   niftyFile);
            Console.WriteLine("\n----------------------------------------------------\n");
            var bankTrades  = RunSymbol("NIFTY BANK", bankFile);

            using (var wb = new XLWorkbook())
            {
                WriteTradeSheet(wb,  "NIFTY 50",          niftyTrades);
                WriteTradeSheet(wb,  "NIFTY BANK",         bankTrades);
                WriteYearlySheet(wb, "Yearly NIFTY 50",   niftyTrades);
                WriteYearlySheet(wb, "Yearly NIFTY BANK",  bankTrades);
                wb.SaveAs(exportPath);
            }

            Console.WriteLine($"\n=== Combined Backtest Complete! ===");
            Console.WriteLine($"Workbook saved to: {exportPath}");
        }

        // ── Core loop ────────────────────────────────────────────────────────
        static List<TradeRecord> RunSymbol(string symbol, string filePath)
        {
            var trades = new List<TradeRecord>();
            if (!File.Exists(filePath)) { Console.WriteLine($"[ERROR] {filePath}"); return trades; }

            Console.WriteLine($"[Loading Data] {filePath}");

            decimal slLimit      = symbol == "NIFTY BANK" ? 150m : 60m;
            decimal minBandWidth = symbol == "NIFTY BANK" ? MinBandWidthBankNifty : MinBandWidthNifty;

            var tracker = new TechnicalIndicatorsTracker();

            bool isLong = false, isShort = false;
            decimal entryPrice = 0, slPrice = 0;
            string  entryStrategy = "", entryPattern = "";
            DateTime entryTime = DateTime.MinValue;

            int totalTrades = 0, wins = 0, losses = 0;
            decimal cumPnL = 0;

            DateTime currentDay  = DateTime.MinValue;
            bool tradedToday     = false;
            decimal dayHigh = decimal.MinValue, dayLow = decimal.MaxValue;

            decimal prevLHHigh = decimal.MinValue, prevLHLow = decimal.MaxValue;
            decimal currLHHigh = decimal.MinValue, currLHLow = decimal.MaxValue;
            bool prevLHReady = false;

            var lastHourStart = new TimeSpan(14, 15, 0);
            var lastHourEnd   = new TimeSpan(15, 15, 0);
            var entryDeadline = new TimeSpan(12, 0, 0);

            Candle? prevCandle = null;

            using var reader = new StreamReader(filePath);
            reader.ReadLine();

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var parts = line.Split(',');
                if (parts.Length < 5) continue;
                if (!DateTime.TryParse(parts[0], out DateTime ct))   continue;
                if (!decimal.TryParse(parts[1],  out decimal open))  continue;
                if (!decimal.TryParse(parts[2],  out decimal high))  continue;
                if (!decimal.TryParse(parts[3],  out decimal low))   continue;
                if (!decimal.TryParse(parts[4],  out decimal close)) continue;

                var tod = ct.TimeOfDay;

                // ── Day rollover ─────────────────────────────────────────────
                if (currentDay == DateTime.MinValue || ct.Date > currentDay.Date)
                {
                    if (currentDay != DateTime.MinValue)
                    {
                        // Commit yesterday's OHLC bounds for 3-Day tracker
                        tracker.AddDailyRange(symbol, dayHigh, dayLow);
                        // Commit yesterday's last-hour band for PDLH
                        prevLHHigh   = currLHHigh;
                        prevLHLow    = currLHLow;
                        prevLHReady  = (prevLHHigh != decimal.MinValue);
                    }
                    currentDay   = ct.Date;
                    tradedToday  = false;
                    dayHigh      = decimal.MinValue;
                    dayLow       = decimal.MaxValue;
                    currLHHigh   = decimal.MinValue;
                    currLHLow    = decimal.MaxValue;
                    prevCandle   = null;
                }

                dayHigh = Math.Max(dayHigh, high);
                dayLow  = Math.Min(dayLow,  low);

                if (tod >= lastHourStart && tod < lastHourEnd)
                {
                    currLHHigh = Math.Max(currLHHigh, high);
                    currLHLow  = Math.Min(currLHLow,  low);
                }

                var currCandle = new Candle { StartTime = ct, Open = open, High = high, Low = low, Close = close };
                var threeDayRange = tracker.GetThreeDayRange(symbol);

                // ── Exit ─────────────────────────────────────────────────────
                if (isLong || isShort)
                {
                    bool hitSL = isLong  ? low  <= slPrice : high >= slPrice;
                    bool eod   = tod >= lastHourEnd;

                    if (hitSL || eod)
                    {
                        decimal exitP = hitSL ? slPrice : close;
                        decimal pnl   = isLong ? (exitP - entryPrice) : (entryPrice - exitP);
                        cumPnL += pnl; totalTrades++;
                        if (pnl > 0) wins++; else losses++;

                        trades.Add(new TradeRecord
                        {
                            Symbol = symbol, Type = isLong ? "Long" : "Short",
                            Strategy = entryStrategy, BreakoutPattern = entryPattern,
                            EntryTime = entryTime, EntryPrice = entryPrice,
                            ExitTime  = ct, ExitPrice = exitP,
                            PnL = pnl, CumulativePnL = cumPnL,
                            ExitReason = hitSL ? "StopLoss(Fixed)" : "EOD SquareOff"
                        });
                        isLong = isShort = false;
                    }
                }

                // ── Entry ─────────────────────────────────────────────────────
                if (!isLong && !isShort && !tradedToday)
                {
                    bool inWindow = tod > new TimeSpan(9, 15, 0) && tod < entryDeadline;

                    if (inWindow)
                    {
                        bool isBullish = close > open;
                        bool isBearish = close < open;

                        // --- Check PDLH signal ---
                        bool pdlhBandOk    = prevLHReady && (prevLHHigh - prevLHLow) >= minBandWidth;
                        bool pdlhLong      = pdlhBandOk && high >= prevLHHigh && isBullish;
                        bool pdlhShort     = pdlhBandOk && low  <= prevLHLow  && isBearish;

                        // --- Check 3-Day signal ---
                        bool threeDayLong  = threeDayRange != null && high >= threeDayRange.Value.High && isBullish;
                        bool threeDayShort = threeDayRange != null && low  <= threeDayRange.Value.Low  && isBearish;

                        bool goLong  = pdlhLong  || threeDayLong;
                        bool goShort = pdlhShort || threeDayShort;

                        if (goLong || goShort)
                        {
                            // Detect pattern
                            var p1 = CandlePatternDetector.DetectSingleCandle(currCandle);
                            var p2 = prevCandle != null 
                                     ? CandlePatternDetector.DetectTwoCandle(prevCandle, currCandle)
                                     : CandlePatternDetector.CandlePattern.None;
                            var finalP = p2 != CandlePatternDetector.CandlePattern.None ? p2 : p1;

                            // Pattern gate
                            if (!AllowedPatterns.Contains(finalP))
                            {
                                prevCandle = currCandle;
                                continue;
                            }

                            entryPattern  = CandlePatternDetector.Describe(finalP);

                            if (goLong)
                            {
                                // Label which strategy fired
                                entryStrategy = (pdlhLong && threeDayLong) ? "PDLH+3Day Long"
                                              : pdlhLong ? "PDLH Long" : "3-Day Long";
                                entryPrice   = pdlhLong
                                               ? Math.Max(open, prevLHHigh)
                                               : Math.Max(open, threeDayRange!.Value.High);
                                slPrice      = entryPrice - slLimit;
                                isLong       = true;
                                entryTime    = ct;
                                tradedToday  = true;
                            }
                            else
                            {
                                entryStrategy = (pdlhShort && threeDayShort) ? "PDLH+3Day Short"
                                              : pdlhShort ? "PDLH Short" : "3-Day Short";
                                entryPrice   = pdlhShort
                                               ? Math.Min(open, prevLHLow)
                                               : Math.Min(open, threeDayRange!.Value.Low);
                                slPrice      = entryPrice + slLimit;
                                isShort      = true;
                                entryTime    = ct;
                                tradedToday  = true;
                            }
                        }
                    }
                }

                prevCandle = currCandle;
            }

            Console.WriteLine($"=== Combined Breakout Report for {symbol} ===");
            Console.WriteLine($"Total Trades:      {totalTrades}");
            Console.WriteLine($"Winning Trades:    {wins}");
            Console.WriteLine($"Losing Trades:     {losses}");
            if (totalTrades > 0)
                Console.WriteLine($"Win Rate:          {((double)wins / totalTrades * 100):F2}%");
            Console.WriteLine($"Cumulative Profit: {cumPnL:F2} Points");

            Console.WriteLine("\n--- Strategy Source Breakdown ---");
            foreach (var g in trades.GroupBy(t => t.Strategy).OrderByDescending(g => g.Sum(x => x.PnL)))
            {
                int cnt  = g.Count();
                int w    = g.Count(t => t.PnL > 0);
                double wr = cnt > 0 ? (double)w / cnt * 100 : 0;
                decimal pl = g.Sum(t => t.PnL);
                Console.WriteLine($"{g.Key,-22} | Trades: {cnt,4} | WinRate: {wr,5:F1}% | PnL: {pl,9:F2}");
            }

            Console.WriteLine("\n--- Pattern Performance ---");
            foreach (var g in trades.GroupBy(t => t.BreakoutPattern).OrderByDescending(g => g.Sum(x => x.PnL)))
            {
                int cnt  = g.Count();
                int w    = g.Count(t => t.PnL > 0);
                double wr = cnt > 0 ? (double)w / cnt * 100 : 0;
                decimal pl = g.Sum(t => t.PnL);
                Console.WriteLine($"{g.Key,-20} | Trades: {cnt,4} | WinRate: {wr,5:F1}% | PnL: {pl,9:F2}");
            }
            Console.WriteLine("----------------------------------\n");

            return trades;
        }

        // ── Excel helpers ────────────────────────────────────────────────────
        static void WriteTradeSheet(XLWorkbook wb, string name, List<TradeRecord> trades)
        {
            var ws = wb.Worksheets.Add(name);
            string[] h = { "Symbol","Type","Strategy","Breakout Pattern","Entry Time","Entry Price","Exit Time","Exit Price","PnL","Cumulative PnL","Exit Reason" };
            for (int i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
            ws.Range(1, 1, 1, h.Length).Style.Font.Bold = true;
            ws.Range(1, 1, 1, h.Length).Style.Fill.BackgroundColor = XLColor.LightGray;

            int row = 2;
            foreach (var t in trades)
            {
                ws.Cell(row, 1).Value  = t.Symbol;
                ws.Cell(row, 2).Value  = t.Type;
                ws.Cell(row, 3).Value  = t.Strategy;
                ws.Cell(row, 4).Value  = t.BreakoutPattern;
                ws.Cell(row, 5).Value  = t.EntryTime.ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(row, 6).Value  = t.EntryPrice;
                ws.Cell(row, 7).Value  = t.ExitTime.ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(row, 8).Value  = t.ExitPrice;
                ws.Cell(row, 9).Value  = t.PnL;
                ws.Cell(row, 9).Style.Font.FontColor = t.PnL > 0 ? XLColor.Green : XLColor.Red;
                ws.Cell(row, 10).Value = t.CumulativePnL;
                ws.Cell(row, 11).Value = t.ExitReason;
                row++;
            }
            ws.Columns().AdjustToContents();
        }

        static void WriteYearlySheet(XLWorkbook wb, string name, List<TradeRecord> trades)
        {
            var ws = wb.Worksheets.Add(name);
            ws.Cell(1,1).Value = "Year"; ws.Cell(1,2).Value = "Total PnL (Points)";
            ws.Cell(1,3).Value = "Total Trades"; ws.Cell(1,4).Value = "Win Rate (%)";
            ws.Range(1,1,1,4).Style.Font.Bold = true;
            ws.Range(1,1,1,4).Style.Fill.BackgroundColor = XLColor.LightYellow;

            int row = 2;
            foreach (var g in trades.GroupBy(t => t.EntryTime.Year).OrderBy(g => g.Key))
            {
                int cnt = g.Count(), w = g.Count(t => t.PnL > 0);
                decimal pnl = g.Sum(t => t.PnL);
                ws.Cell(row, 1).Value = g.Key;
                ws.Cell(row, 2).Value = pnl;
                ws.Cell(row, 2).Style.Font.FontColor = pnl >= 0 ? XLColor.Green : XLColor.Red;
                ws.Cell(row, 3).Value = cnt;
                ws.Cell(row, 4).Value = cnt > 0 ? Math.Round((double)w / cnt * 100, 2) : 0;
                row++;
            }
            ws.Columns().AdjustToContents();
        }
    }
}
