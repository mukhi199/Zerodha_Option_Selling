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
    /// Optimized Combined Engine: PDLH + 3-Day Breakout
    /// Production settings derived from 1,296-config grid scan:
    ///   Nifty  50  → EMA20 trend gate | SL  60pts | MinBand 40pts | Entry deadline 12:00
    ///   Nifty BANK → EMA50 trend gate | SL 100pts | MinBand 80pts | Entry deadline 12:00
    ///   Patterns   → Marubozu + Engulfing only
    /// </summary>
    public static class CombinedBreakoutBacktest
    {
        // ── Pattern whitelist ─────────────────────────────────────────────────
        private static readonly HashSet<CandlePatternDetector.CandlePattern> AllowedPatterns = new()
        {
            CandlePatternDetector.CandlePattern.BullishMarubozu,
            CandlePatternDetector.CandlePattern.BearishMarubozu,
            CandlePatternDetector.CandlePattern.BullishEngulfing,
            CandlePatternDetector.CandlePattern.BearishEngulfing,
        };

        public static void Run()
        {
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║   OPTIMIZED COMBINED ENGINE (PDLH + 3-DAY + EMA)        ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════╝\n");

            var root       = "/Users/Lenovo/Projects/Zerodha_Option_Selling";
            var niftyFile  = Path.Combine(root, "NIFTY 50_5minute.csv");
            var bankFile   = Path.Combine(root, "NIFTY BANK_5minute.csv");
            var exportPath = Path.Combine(root, "Backtest_Results_Combined.xlsx");

            // Optimal configs per scanner results
            var niftyTrades = RunSymbol("NIFTY 50",   niftyFile,  slLimit: 60m,  emaPeriod: 20, minBandWidth: 40m);
            Console.WriteLine("\n----------------------------------------------------\n");
            var bankTrades  = RunSymbol("NIFTY BANK", bankFile,   slLimit: 100m, emaPeriod: 50, minBandWidth: 80m);

            using (var wb = new XLWorkbook())
            {
                WriteTradeSheet(wb,  "NIFTY 50",          niftyTrades);
                WriteTradeSheet(wb,  "NIFTY BANK",         bankTrades);
                WriteYearlySheet(wb, "Yearly NIFTY 50",   niftyTrades);
                WriteYearlySheet(wb, "Yearly NIFTY BANK",  bankTrades);
                wb.SaveAs(exportPath);
            }

            Console.WriteLine($"\n=== Optimized Combined Backtest Complete! ===");
            Console.WriteLine($"Workbook saved to: {exportPath}");
        }

        static List<TradeRecord> RunSymbol(
            string symbol, string filePath,
            decimal slLimit, int emaPeriod, decimal minBandWidth)
        {
            var trades = new List<TradeRecord>();
            if (!File.Exists(filePath)) { Console.WriteLine($"[ERROR] {filePath}"); return trades; }
            Console.WriteLine($"[Loading] {filePath}  (EMA{emaPeriod} | SL={slLimit} | MinBand={minBandWidth})");

            var tracker    = new TechnicalIndicatorsTracker();
            var entryDeadline = new TimeSpan(12, 0, 0);
            var lastHourStart = new TimeSpan(14, 15, 0);
            var lastHourEnd   = new TimeSpan(15, 15, 0);

            // EMA state
            decimal emaAlpha = 2m / (emaPeriod + 1);
            decimal emaValue = 0;
            bool    emaReady = false;

            bool    isLong = false, isShort = false;
            decimal entryPrice = 0, slPrice = 0;
            string  entryStrategy = "", entryPattern = "";
            DateTime entryTime = DateTime.MinValue;

            int totalTrades = 0, wins = 0, losses = 0;
            decimal cumPnL = 0;

            DateTime currentDay  = DateTime.MinValue;
            int      tradesToday = 0;
            decimal  dayHigh = decimal.MinValue, dayLow = decimal.MaxValue;
            decimal  prevDayH = 0, prevDayL = 0;

            decimal prevLHHigh = decimal.MinValue, prevLHLow = decimal.MaxValue;
            decimal currLHHigh = decimal.MinValue, currLHLow = decimal.MaxValue;
            bool    prevLHReady = false;

            int candlesToday = 0;
            decimal orbHigh = decimal.MinValue, orbLow = decimal.MaxValue, orbOpen = 0, orbClose = 0;
            bool orbSet = false, isGapUp = false, isGapDown = false, orbIsBearish = false, orbIsBullish = false;
            var orbEnd = new TimeSpan(9, 45, 0);

            Candle? prevCandle = null;

            using var reader = new StreamReader(filePath);
            reader.ReadLine();

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var  p     = line.Split(',');
                if (p.Length < 5) continue;
                if (!DateTime.TryParse(p[0], out DateTime ct))   continue;
                if (!decimal.TryParse(p[1],  out decimal open))  continue;
                if (!decimal.TryParse(p[2],  out decimal high))  continue;
                if (!decimal.TryParse(p[3],  out decimal low))   continue;
                if (!decimal.TryParse(p[4],  out decimal close)) continue;

                // ── EMA update ───────────────────────────────────────────────
                if (!emaReady) { emaValue = close; emaReady = true; }
                else           { emaValue = emaAlpha * close + (1 - emaAlpha) * emaValue; }

                var tod = ct.TimeOfDay;

                // ── Day rollover ─────────────────────────────────────────────
                if (currentDay == DateTime.MinValue || ct.Date > currentDay.Date)
                {
                    if (currentDay != DateTime.MinValue)
                    {
                        tracker.AddDailyRange(symbol, dayHigh, dayLow);
                        prevLHHigh  = currLHHigh;
                        prevLHLow   = currLHLow;
                        prevLHReady = prevLHHigh != decimal.MinValue;
                        prevDayH    = dayHigh;
                        prevDayL    = dayLow;
                    }
                    currentDay   = ct.Date;
                    tradesToday  = 0;
                    dayHigh      = decimal.MinValue;
                    dayLow       = decimal.MaxValue;
                    currLHHigh   = decimal.MinValue;
                    currLHLow    = decimal.MaxValue;
                    prevCandle   = null;

                    candlesToday = 0;
                    orbHigh = decimal.MinValue; orbLow = decimal.MaxValue;
                    orbSet = false;
                    isGapUp = prevDayH > 0 && open > prevDayH;
                    isGapDown = prevDayL > 0 && open < prevDayL;
                    orbOpen = open;
                }

                dayHigh = Math.Max(dayHigh, high);
                dayLow  = Math.Min(dayLow,  low);

                if (tod >= lastHourStart && tod < lastHourEnd)
                {
                    currLHHigh = Math.Max(currLHHigh, high);
                    currLHLow  = Math.Min(currLHLow,  low);
                }

                if (tod <= orbEnd)
                {
                    candlesToday++;
                    orbHigh = Math.Max(orbHigh, high);
                    orbLow  = Math.Min(orbLow,  low);
                    if (candlesToday == 3)
                    {
                        orbClose = close; orbSet = true;
                        orbIsBearish = orbClose < orbOpen;
                        orbIsBullish = orbClose > orbOpen;
                    }
                }

                var currCandle    = new Candle { StartTime = ct, Open = open, High = high, Low = low, Close = close };
                var threeDayRange = tracker.GetThreeDayRange(symbol);

                // ── Exit ──────────────────────────────────────────────────────
                if (isLong || isShort)
                {
                    bool hitSL = isLong ? low <= slPrice : high >= slPrice;
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
                if (!isLong && !isShort && tradesToday < 2 && emaReady)
                {
                    bool inWindow = tod > new TimeSpan(9, 15, 0) && tod < entryDeadline;
                    bool bandOk   = prevLHReady && (prevLHHigh - prevLHLow) >= minBandWidth;

                    if (inWindow)
                    {
                        bool isBullish    = close > open;
                        bool isBearish    = close < open;
                        // EMA trend gate: long only above EMA, short only below EMA
                        bool emaTrending  = emaReady;
                        bool emaBullish   = emaTrending && close > emaValue;
                        bool emaBearish   = emaTrending && close < emaValue;

                        bool pdlhLong  = bandOk && high >= prevLHHigh && isBullish && emaBullish;
                        bool pdlhShort = bandOk && low  <= prevLHLow  && isBearish && emaBearish;
                        bool tdLong    = threeDayRange != null && high >= threeDayRange.Value.High && isBullish && emaBullish;
                        bool tdShort   = threeDayRange != null && low  <= threeDayRange.Value.Low  && isBearish && emaBearish;

                        bool gapOrbLong  = orbSet && isGapDown && orbIsBullish && high >= orbHigh && isBullish;
                        bool gapOrbShort = orbSet && isGapUp   && orbIsBearish && low  <= orbLow  && isBearish;

                        bool goLong  = pdlhLong  || tdLong  || gapOrbLong;
                        bool goShort = pdlhShort || tdShort || gapOrbShort;

                        if (goLong || goShort)
                        {
                            // Pattern gate
                            var p1     = CandlePatternDetector.DetectSingleCandle(currCandle);
                            var p2     = prevCandle != null
                                         ? CandlePatternDetector.DetectTwoCandle(prevCandle, currCandle)
                                         : CandlePatternDetector.CandlePattern.None;
                            var finalP = p2 != CandlePatternDetector.CandlePattern.None ? p2 : p1;

                            if (!AllowedPatterns.Contains(finalP))
                            {
                                prevCandle = currCandle;
                                continue;
                            }

                            entryPattern = CandlePatternDetector.Describe(finalP);

                            if (goLong)
                            {
                                entryStrategy = gapOrbLong ? "Gap Reversal ORB Long" :
                                                (pdlhLong && tdLong) ? "PDLH+3Day Long" :
                                                pdlhLong ? "PDLH Long" : "3-Day Long";
                                entryPrice   = gapOrbLong ? Math.Max(open, orbHigh) :
                                               pdlhLong ? Math.Max(open, prevLHHigh) :
                                               Math.Max(open, threeDayRange!.Value.High);
                                slPrice      = gapOrbLong ? orbLow : entryPrice - slLimit;
                                isLong       = true;
                                entryTime    = ct;
                                tradesToday++;
                            }
                            else
                            {
                                entryStrategy = gapOrbShort ? "Gap Reversal ORB Short" :
                                                (pdlhShort && tdShort) ? "PDLH+3Day Short" :
                                                pdlhShort ? "PDLH Short" : "3-Day Short";
                                entryPrice   = gapOrbShort ? Math.Min(open, orbLow) :
                                               pdlhShort ? Math.Min(open, prevLHLow) :
                                               Math.Min(open, threeDayRange!.Value.Low);
                                slPrice      = gapOrbShort ? orbHigh : entryPrice + slLimit;
                                isShort      = true;
                                entryTime    = ct;
                                tradesToday++;
                            }
                        }
                    }
                }

                prevCandle = currCandle;
            }

            // Console Report
            Console.WriteLine($"\n══════ Optimized Combined Report: {symbol} ══════");
            Console.WriteLine($"EMA Period:        EMA{emaPeriod}");
            Console.WriteLine($"Stop Loss:         {slLimit} pts");
            Console.WriteLine($"Min PDLH Band:     {minBandWidth} pts");
            Console.WriteLine($"Total Trades:      {totalTrades}");
            Console.WriteLine($"Winning Trades:    {wins}");
            Console.WriteLine($"Losing Trades:     {losses}");
            if (totalTrades > 0)
                Console.WriteLine($"Win Rate:          {((double)wins / totalTrades * 100):F2}%");
            Console.WriteLine($"Cumulative Profit: {cumPnL:F2} Points");

            // Strategy source breakdown
            Console.WriteLine("\n--- Strategy Source ---");
            foreach (var g in trades.GroupBy(t => t.Strategy).OrderByDescending(g => g.Sum(x => x.PnL)))
            {
                int cnt = g.Count(), w = g.Count(t => t.PnL > 0);
                double wr = cnt > 0 ? (double)w / cnt * 100 : 0;
                decimal pl = g.Sum(t => t.PnL);
                Console.WriteLine($"{g.Key,-22} | {cnt,4} trades | {wr,5:F1}% WR | {pl,9:F2} pts");
            }

            // Yearly rollup
            Console.WriteLine("\n--- Yearly Breakdown ---");
            foreach (var g in trades.GroupBy(t => t.EntryTime.Year).OrderBy(g => g.Key))
            {
                int cnt = g.Count(), w = g.Count(t => t.PnL > 0);
                double wr = cnt > 0 ? (double)w / cnt * 100 : 0;
                decimal pl = g.Sum(t => t.PnL);
                Console.WriteLine($"{g.Key} | {cnt,4} trades | {wr,5:F1}% WR | {pl,8:F2} pts");
            }
            Console.WriteLine();

            return trades;
        }

        // ── Excel Helpers ─────────────────────────────────────────────────────
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
