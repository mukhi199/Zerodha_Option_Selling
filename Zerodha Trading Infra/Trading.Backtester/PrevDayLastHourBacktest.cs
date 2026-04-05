using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Trading.Core.Models;
using Trading.Core.Utils;
using ClosedXML.Excel;

namespace Trading.Backtester
{
    /// <summary>
    /// Previous Day Last-Hour (PDLH) Breakout Strategy
    /// Entry Rules:
    ///   - Long : today's candle breaks ABOVE prev-day's last-hour HIGH with a Bullish close
    ///   - Short: today's candle breaks BELOW prev-day's last-hour LOW  with a Bearish close
    /// Exit Rules:
    ///   - Fixed SL: Long = entry - 60pts (Nifty) / 150pts (BankNifty)
    ///               Short= entry + 60pts (Nifty) / 150pts (BankNifty)
    ///   - EOD square-off at 15:15
    ///   - Max 1 trade per day
    /// </summary>
    public static class PrevDayLastHourBacktest
    {
        public static void Run()
        {
            Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║   PREV DAY LAST-HOUR (PDLH) BREAKOUT BACKTEST       ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝\n");

            var rootFolder = "/Users/Lenovo/Projects/Zerodha_Option_Selling";
            var nifty50File    = Path.Combine(rootFolder, "NIFTY 50_5minute.csv");
            var bankNiftyFile  = Path.Combine(rootFolder, "NIFTY BANK_5minute.csv");
            var exportPath     = Path.Combine(rootFolder, "Backtest_Results_PDLH.xlsx");

            var niftyTrades   = RunSymbol("NIFTY 50",   nifty50File);
            Console.WriteLine("\n----------------------------------------------------\n");
            var bankTrades    = RunSymbol("NIFTY BANK", bankNiftyFile);

            using (var wb = new XLWorkbook())
            {
                WriteTradeSheet(wb, "NIFTY 50",        niftyTrades);
                WriteTradeSheet(wb, "NIFTY BANK",      bankTrades);
                WriteYearlySheet(wb, "Yearly NIFTY 50",   niftyTrades);
                WriteYearlySheet(wb, "Yearly NIFTY BANK", bankTrades);
                wb.SaveAs(exportPath);
            }

            Console.WriteLine($"\n=== PDLH Backtest Complete! ===");
            Console.WriteLine($"Workbook saved to: {exportPath}");
        }

        // ─── Quality Filters ─────────────────────────────────────────────────
        // Only these patterns are considered high-conviction breakouts
        // Only the two highest-conviction breakout structures
        private static readonly HashSet<CandlePatternDetector.CandlePattern> AllowedPatterns = new()
        {
            CandlePatternDetector.CandlePattern.BullishMarubozu,
            CandlePatternDetector.CandlePattern.BearishMarubozu,
            CandlePatternDetector.CandlePattern.BullishEngulfing,
            CandlePatternDetector.CandlePattern.BearishEngulfing,
        };

        // Minimum width of the PDLH band — too narrow = random noise
        const decimal MinBandWidthNifty    = 60m;
        const decimal MinBandWidthBankNifty = 120m;

        // No entries after this time — afternoon chop zone
        static readonly TimeSpan EntryDeadline = new TimeSpan(12, 0, 0);

        // ─── Core Backtest ───────────────────────────────────────────────────

        static List<TradeRecord> RunSymbol(string symbol, string filePath)
        {
            var trades = new List<TradeRecord>();
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[ERROR] File not found: {filePath}");
                return trades;
            }

            Console.WriteLine($"[Loading Data] {filePath}");

            decimal slLimit      = symbol == "NIFTY BANK" ? 150m : 60m;
            decimal minBandWidth = symbol == "NIFTY BANK" ? MinBandWidthBankNifty : MinBandWidthNifty;

            // In-flight trade state
            bool isLong = false, isShort = false;
            decimal entryPrice = 0, slPrice = 0;
            string entryPattern = "";
            DateTime entryTime = DateTime.MinValue;

            int totalTrades = 0, wins = 0, losses = 0;
            decimal cumPnL = 0;

            // Daily tracking
            DateTime currentDay = DateTime.MinValue;
            bool tradedToday = false;

            // Last-hour of the PREVIOUS day (14:15 – 15:15)
            decimal prevLastHourHigh = decimal.MinValue;
            decimal prevLastHourLow  = decimal.MaxValue;
            bool prevLastHourReady   = false;

            // Accumulate the last-hour candles of the CURRENT day (used at rollover)
            decimal currLastHourHigh = decimal.MinValue;
            decimal currLastHourLow  = decimal.MaxValue;

            Candle? prevCandle = null;

            // Last-hour window boundaries
            var lastHourStart = new TimeSpan(14, 15, 0);
            var lastHourEnd   = new TimeSpan(15, 15, 0);

            using var reader = new StreamReader(filePath);
            reader.ReadLine();  // skip header

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var p = line.Split(',');
                if (p.Length < 5) continue;
                if (!DateTime.TryParse(p[0], out DateTime ct))  continue;
                if (!decimal.TryParse(p[1], out decimal open))  continue;
                if (!decimal.TryParse(p[2], out decimal high))  continue;
                if (!decimal.TryParse(p[3], out decimal low))   continue;
                if (!decimal.TryParse(p[4], out decimal close)) continue;

                var tod = ct.TimeOfDay;

                // ── Day rollover ─────────────────────────────────────────────
                if (currentDay == DateTime.MinValue || ct.Date > currentDay.Date)
                {
                    // At session end, commit today's last-hour band as tomorrow's 'prev'
                    if (currentDay != DateTime.MinValue)
                    {
                        prevLastHourHigh = currLastHourHigh;
                        prevLastHourLow  = currLastHourLow;
                        prevLastHourReady = (prevLastHourHigh != decimal.MinValue);
                    }

                    currentDay       = ct.Date;
                    tradedToday      = false;
                    currLastHourHigh = decimal.MinValue;
                    currLastHourLow  = decimal.MaxValue;
                    prevCandle       = null;
                }

                // ── Track current day's last-hour band ───────────────────────
                if (tod >= lastHourStart && tod < lastHourEnd)
                {
                    currLastHourHigh = Math.Max(currLastHourHigh, high);
                    currLastHourLow  = Math.Min(currLastHourLow,  low);
                }

                var currCandle = new Candle
                {
                    StartTime = ct,
                    Open = open, High = high, Low = low, Close = close
                };

                // ── Exit management ──────────────────────────────────────────
                if (isLong)
                {
                    bool hitSL  = low  <= slPrice;
                    bool eod    = tod  >= lastHourEnd;

                    if (hitSL || eod)
                    {
                        decimal exitP  = hitSL ? slPrice : close;
                        decimal pnl    = exitP - entryPrice;
                        cumPnL += pnl; totalTrades++;
                        if (pnl > 0) wins++; else losses++;

                        trades.Add(new TradeRecord
                        {
                            Symbol = symbol, Type = "Long", Strategy = "PDLH Breakout",
                            BreakoutPattern = entryPattern,
                            EntryTime = entryTime, EntryPrice = entryPrice,
                            ExitTime = ct, ExitPrice = exitP,
                            PnL = pnl, CumulativePnL = cumPnL,
                            ExitReason = hitSL ? "StopLoss(Fixed)" : "EOD SquareOff"
                        });
                        isLong = false;
                    }
                }
                else if (isShort)
                {
                    bool hitSL  = high >= slPrice;
                    bool eod    = tod  >= lastHourEnd;

                    if (hitSL || eod)
                    {
                        decimal exitP  = hitSL ? slPrice : close;
                        decimal pnl    = entryPrice - exitP;
                        cumPnL += pnl; totalTrades++;
                        if (pnl > 0) wins++; else losses++;

                        trades.Add(new TradeRecord
                        {
                            Symbol = symbol, Type = "Short", Strategy = "PDLH Breakout",
                            BreakoutPattern = entryPattern,
                            EntryTime = entryTime, EntryPrice = entryPrice,
                            ExitTime = ct, ExitPrice = exitP,
                            PnL = pnl, CumulativePnL = cumPnL,
                            ExitReason = hitSL ? "StopLoss(Fixed)" : "EOD SquareOff"
                        });
                        isShort = false;
                    }
                }

                // ── Entry logic (1 trade/day, after ORB window, needs prior data) ─
                if (!isLong && !isShort && !tradedToday && prevLastHourReady)
                {
                    // Only trade after morning range is established (post 10:15)
                    // and before the last hour starts
                    bool inWindow = tod > new TimeSpan(9, 15, 0) && tod < EntryDeadline;  // ← 12:00 PM gate

                    // ── Filter 1: Minimum PDLH band width ───────────────────────
                    bool bandWideEnough = (prevLastHourHigh - prevLastHourLow) >= minBandWidth;

                    if (inWindow && bandWideEnough)
                    {
                        bool isBullish = close > open;
                        bool isBearish = close < open;

                        bool longSignal  = high  >= prevLastHourHigh && isBullish;
                        bool shortSignal = low   <= prevLastHourLow  && isBearish;

                        if (longSignal || shortSignal)
                        {
                            // Detect candlestick pattern
                            var p1   = CandlePatternDetector.DetectSingleCandle(currCandle);
                            var p2   = prevCandle != null
                                       ? CandlePatternDetector.DetectTwoCandle(prevCandle, currCandle)
                                       : CandlePatternDetector.CandlePattern.None;
                            var finalP = p2 != CandlePatternDetector.CandlePattern.None ? p2 : p1;
                            entryPattern = CandlePatternDetector.Describe(finalP);

                            // ── Filter 2: Pattern whitelist (no Doji, no Long-Wicks, no ShootingStars) ──
                            if (!AllowedPatterns.Contains(finalP))
                            {
                                prevCandle = currCandle;
                                continue;  // Skip this signal — low-conviction candle
                            }
                        }

                        if (longSignal)
                        {
                            isLong       = true;
                            entryPrice   = Math.Max(open, prevLastHourHigh); // fill at band or open
                            slPrice      = entryPrice - slLimit;
                            entryTime    = ct;
                            tradedToday  = true;
                        }
                        else if (shortSignal)
                        {
                            isShort      = true;
                            entryPrice   = Math.Min(open, prevLastHourLow);
                            slPrice      = entryPrice + slLimit;
                            entryTime    = ct;
                            tradedToday  = true;
                        }
                    }
                }

                prevCandle = currCandle;
            }

            // Print console summary
            Console.WriteLine($"=== PDLH Breakout Report for {symbol} ===");
            Console.WriteLine($"Total Trades:      {totalTrades}");
            Console.WriteLine($"Winning Trades:    {wins}");
            Console.WriteLine($"Losing Trades:     {losses}");
            if (totalTrades > 0)
                Console.WriteLine($"Win Rate:          {((double)wins / totalTrades * 100):F2}%");
            Console.WriteLine($"Cumulative Profit: {cumPnL:F2} Points");

            Console.WriteLine("\n--- PDLH Pattern Performance ---");
            var groups = trades.GroupBy(t => t.BreakoutPattern)
                               .OrderByDescending(g => g.Count() > 0 ? (double)g.Count(x => x.PnL > 0) / g.Count() : 0);
            foreach (var g in groups)
            {
                int cnt  = g.Count();
                int w    = g.Count(t => t.PnL > 0);
                double wr = cnt > 0 ? (double)w / cnt * 100 : 0;
                decimal pl = g.Sum(t => t.PnL);
                Console.WriteLine($"{g.Key,-20} | Trades: {cnt,4} | WinRate: {wr,5:F1}% | PnL: {pl,9:F2}");
            }
            Console.WriteLine("--------------------------------\n");

            return trades;
        }

        // ─── Excel Helpers ───────────────────────────────────────────────────

        static void WriteTradeSheet(XLWorkbook wb, string name, List<TradeRecord> trades)
        {
            var ws = wb.Worksheets.Add(name);
            string[] headers = { "Symbol","Type","Strategy","Breakout Pattern",
                                  "Entry Time","Entry Price","Exit Time","Exit Price",
                                  "PnL","Cumulative PnL","Exit Reason" };
            for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];

            var hdr = ws.Range(1, 1, 1, headers.Length);
            hdr.Style.Font.Bold = true;
            hdr.Style.Fill.BackgroundColor = XLColor.LightGray;

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
            ws.Cell(1, 1).Value = "Year";
            ws.Cell(1, 2).Value = "Total PnL (Points)";
            ws.Cell(1, 3).Value = "Total Trades";
            ws.Cell(1, 4).Value = "Win Rate (%)";

            var hdr = ws.Range(1, 1, 1, 4);
            hdr.Style.Font.Bold = true;
            hdr.Style.Fill.BackgroundColor = XLColor.LightYellow;

            int row = 2;
            foreach (var g in trades.GroupBy(t => t.EntryTime.Year).OrderBy(g => g.Key))
            {
                int cnt  = g.Count();
                int wins = g.Count(t => t.PnL > 0);
                decimal pnl = g.Sum(t => t.PnL);

                ws.Cell(row, 1).Value = g.Key;
                ws.Cell(row, 2).Value = pnl;
                ws.Cell(row, 2).Style.Font.FontColor = pnl >= 0 ? XLColor.Green : XLColor.Red;
                ws.Cell(row, 3).Value = cnt;
                ws.Cell(row, 4).Value = cnt > 0 ? Math.Round((double)wins / cnt * 100, 2) : 0;
                row++;
            }
            ws.Columns().AdjustToContents();
        }
    }
}
