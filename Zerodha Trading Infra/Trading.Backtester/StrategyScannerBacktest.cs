using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Trading.Core.Models;
using Trading.Core.Utils;
using Trading.Strategy.Services;

namespace Trading.Backtester
{
    /// <summary>
    /// Strategy Parameter Scanner
    /// Tests multiple independent variable combinations to find the true
    /// highest Win Rate + PnL configuration from the raw historical data.
    ///
    /// Variables tested:
    ///   A. EMA alignment gate (EMA20/EMA50 direction confirmation)
    ///   B. Stop Loss magnitude (Nifty: 40/60/80/100 | BankNifty: 100/150/200/250)
    ///   C. Minimum PDLH band width (Nifty: 40/60/80 | BankNifty: 80/120/160)
    ///   D. Entry time deadline (10:30 / 11:00 / 12:00)
    /// </summary>
    public static class StrategyScannerBacktest
    {
        static readonly string RootFolder = "/Users/Lenovo/Projects/Zerodha_Option_Selling";

        record ScanConfig(
            string Label,
            bool UseEmaFilter,
            int EmaPeriod,
            decimal SlNifty, decimal SlBank,
            decimal MinBandNifty, decimal MinBandBank,
            TimeSpan EntryDeadline
        );

        record ScanResult(string Symbol, string Config, int Trades, int Wins, decimal PnL)
        {
            public double WinRate => Trades > 0 ? (double)Wins / Trades * 100 : 0;
        }

        public static void Run()
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║   STRATEGY PARAMETER SCANNER (Deep Data Analysis)     ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝\n");

            // Build a grid of configurations to test
            var configs = new List<ScanConfig>();
            bool[] emaOptions  = { false, true };
            int[]  emaPeriods  = { 20, 50 };
            decimal[] slNifty  = { 40m,  60m,  80m,  100m };
            decimal[] slBank   = { 100m, 150m, 200m, 250m };
            decimal[] bwNifty  = { 40m,  60m,  80m };
            decimal[] bwBank   = { 80m,  120m, 160m };
            var deadlines      = new[] { new TimeSpan(10,30,0), new TimeSpan(11,0,0), new TimeSpan(12,0,0) };

            foreach (var useEma in emaOptions)
            foreach (var ema   in (useEma ? emaPeriods : new[]{ 0 }))
            foreach (var sln   in slNifty)
            foreach (var slb   in slBank)
            foreach (var bwn   in bwNifty)
            foreach (var bwb   in bwBank)
            foreach (var dl    in deadlines)
            {
                string label = $"EMA{(useEma ? ema.ToString() : "OFF")}_SL{sln}/{slb}_BW{bwn}/{bwb}_DL{dl:hh\\:mm}";
                configs.Add(new ScanConfig(label, useEma, ema, sln, slb, bwn, bwb, dl));
            }

            Console.WriteLine($"Total configurations to test: {configs.Count}");
            Console.WriteLine("Scanning...\n");

            var niftyFile = Path.Combine(RootFolder, "NIFTY 50_5minute.csv");
            var bankFile  = Path.Combine(RootFolder, "NIFTY BANK_5minute.csv");

            var allResults = new List<ScanResult>();

            foreach (var cfg in configs)
            {
                var nr = RunSingle("NIFTY 50",   niftyFile, cfg.UseEmaFilter, cfg.EmaPeriod, cfg.SlNifty, cfg.MinBandNifty, cfg.EntryDeadline, cfg.Label);
                var br = RunSingle("NIFTY BANK", bankFile,  cfg.UseEmaFilter, cfg.EmaPeriod, cfg.SlBank,  cfg.MinBandBank,  cfg.EntryDeadline, cfg.Label);
                allResults.Add(nr);
                allResults.Add(br);
            }

            // ── Top 10 by PnL : Nifty ───────────────────────────────────────
            PrintTopN("NIFTY 50 — Top 10 by Total Points", allResults, "NIFTY 50", sortByPnL: true);
            PrintTopN("NIFTY 50 — Top 10 by Win Rate",     allResults, "NIFTY 50", sortByPnL: false);
            PrintTopN("NIFTY BANK — Top 10 by Total Points", allResults, "NIFTY BANK", sortByPnL: true);
            PrintTopN("NIFTY BANK — Top 10 by Win Rate",     allResults, "NIFTY BANK", sortByPnL: false);

            // ── Combined score (normalized PnL + Win Rate) ───────────────────
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║           TOP 5 COMBINED SCORE (Nifty + BankNifty)              ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════╝");

            var combined = allResults
                .GroupBy(r => r.Config)
                .Select(g => new {
                    Config    = g.Key,
                    TotalPnL  = g.Sum(r => r.PnL),
                    AvgWR     = g.Average(r => r.WinRate),
                    TotalTrades = g.Sum(r => r.Trades)
                })
                .OrderByDescending(x => x.TotalPnL + (decimal)(x.AvgWR * 500))
                .Take(5);

            Console.WriteLine($"\n{"Config",-60} | {"Trades",7} | {"AvgWR",7} | {"TotalPnL",10}");
            Console.WriteLine(new string('-', 100));
            foreach (var r in combined)
                Console.WriteLine($"{r.Config,-60} | {r.TotalTrades,7} | {r.AvgWR,6:F1}% | {r.TotalPnL,10:F2}");
            Console.WriteLine();
        }

        static void PrintTopN(string title, List<ScanResult> all, string symbol, bool sortByPnL)
        {
            Console.WriteLine($"\n╔══ {title} ══╗");
            var rows = all.Where(r => r.Symbol == symbol);
            IOrderedEnumerable<ScanResult> sorted = sortByPnL
                ? rows.OrderByDescending(r => r.PnL)
                : rows.OrderByDescending(r => r.WinRate);
            var top = sorted.Take(10);

            Console.WriteLine($"{"Config",-60} | {"Trades",7} | {"WinRate",8} | {"PnL",10}");
            Console.WriteLine(new string('-', 95));
            foreach (var r in top)
                Console.WriteLine($"{r.Config,-60} | {r.Trades,7} | {r.WinRate,7:F1}% | {r.PnL,10:F2}");
        }

        // ── Core single-run engine ───────────────────────────────────────────
        static ScanResult RunSingle(
            string symbol, string filePath,
            bool useEma, int emaPeriod,
            decimal slLimit, decimal minBandWidth,
            TimeSpan entryDeadline, string configLabel)
        {
            if (!File.Exists(filePath)) return new ScanResult(symbol, configLabel, 0, 0, 0);

            var tracker = new TechnicalIndicatorsTracker();

            // Rolling EMA
            decimal emaValue = 0;
            decimal emaAlpha = emaPeriod > 0 ? 2m / (emaPeriod + 1) : 0;
            bool emaSeeded = false;

            bool isLong = false, isShort = false;
            decimal entryPrice = 0, slPrice = 0;

            int trades = 0, wins = 0;
            decimal cumPnL = 0;

            DateTime currentDay = DateTime.MinValue;
            bool tradedToday = false;
            decimal dayHigh = decimal.MinValue, dayLow = decimal.MaxValue;

            decimal prevLHHigh = decimal.MinValue, prevLHLow = decimal.MaxValue;
            decimal currLHHigh = decimal.MinValue, currLHLow = decimal.MaxValue;
            bool prevLHReady = false;

            var lastHourStart = new TimeSpan(14, 15, 0);
            var lastHourEnd   = new TimeSpan(15, 15, 0);

            Candle? prevCandle = null;

            using var reader = new StreamReader(filePath);
            reader.ReadLine();

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var p = line.Split(',');
                if (p.Length < 5) continue;
                if (!DateTime.TryParse(p[0], out DateTime ct))   continue;
                if (!decimal.TryParse(p[1],  out decimal open))  continue;
                if (!decimal.TryParse(p[2],  out decimal high))  continue;
                if (!decimal.TryParse(p[3],  out decimal low))   continue;
                if (!decimal.TryParse(p[4],  out decimal close)) continue;

                // Update EMA
                if (!emaSeeded) { emaValue = close; emaSeeded = true; }
                else if (emaPeriod > 0) emaValue = emaAlpha * close + (1 - emaAlpha) * emaValue;

                var tod = ct.TimeOfDay;

                if (currentDay == DateTime.MinValue || ct.Date > currentDay.Date)
                {
                    if (currentDay != DateTime.MinValue)
                    {
                        tracker.AddDailyRange(symbol, dayHigh, dayLow);
                        prevLHHigh  = currLHHigh;
                        prevLHLow   = currLHLow;
                        prevLHReady = (prevLHHigh != decimal.MinValue);
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

                var currCandle    = new Candle { StartTime = ct, Open = open, High = high, Low = low, Close = close };
                var threeDayRange = tracker.GetThreeDayRange(symbol);

                // Exit
                if (isLong || isShort)
                {
                    bool hitSL = isLong ? low <= slPrice : high >= slPrice;
                    bool eod   = tod >= lastHourEnd;
                    if (hitSL || eod)
                    {
                        decimal exitP = hitSL ? slPrice : close;
                        decimal pnl   = isLong ? (exitP - entryPrice) : (entryPrice - exitP);
                        cumPnL += pnl; trades++;
                        if (pnl > 0) wins++;
                        isLong = isShort = false;
                    }
                }

                // Entry
                if (!isLong && !isShort && !tradedToday)
                {
                    bool inWindow = tod > new TimeSpan(9, 15, 0) && tod < entryDeadline;
                    bool bandOk   = prevLHReady && (prevLHHigh - prevLHLow) >= minBandWidth;

                    if (inWindow)
                    {
                        bool isBullish = close > open;
                        bool isBearish = close < open;

                        bool emaBullish = !useEma || (emaSeeded && close > emaValue);
                        bool emaBearish = !useEma || (emaSeeded && close < emaValue);

                        bool pdlhLong  = bandOk && high >= prevLHHigh && isBullish && emaBullish;
                        bool pdlhShort = bandOk && low  <= prevLHLow  && isBearish && emaBearish;
                        bool tdLong    = threeDayRange != null && high >= threeDayRange.Value.High && isBullish && emaBullish;
                        bool tdShort   = threeDayRange != null && low  <= threeDayRange.Value.Low  && isBearish && emaBearish;

                        bool goLong  = pdlhLong  || tdLong;
                        bool goShort = pdlhShort || tdShort;

                        if (goLong || goShort)
                        {
                            var p1     = CandlePatternDetector.DetectSingleCandle(currCandle);
                            var p2     = prevCandle != null ? CandlePatternDetector.DetectTwoCandle(prevCandle, currCandle) : CandlePatternDetector.CandlePattern.None;
                            var finalP = p2 != CandlePatternDetector.CandlePattern.None ? p2 : p1;

                            bool patternOk = finalP is CandlePatternDetector.CandlePattern.BullishMarubozu
                                                      or CandlePatternDetector.CandlePattern.BearishMarubozu
                                                      or CandlePatternDetector.CandlePattern.BullishEngulfing
                                                      or CandlePatternDetector.CandlePattern.BearishEngulfing;

                            if (!patternOk) { prevCandle = currCandle; continue; }

                            if (goLong)
                            {
                                isLong      = true;
                                entryPrice  = pdlhLong ? Math.Max(open, prevLHHigh) : Math.Max(open, threeDayRange!.Value.High);
                                slPrice     = entryPrice - slLimit;
                                tradedToday = true;
                            }
                            else
                            {
                                isShort     = true;
                                entryPrice  = pdlhShort ? Math.Min(open, prevLHLow) : Math.Min(open, threeDayRange!.Value.Low);
                                slPrice     = entryPrice + slLimit;
                                tradedToday = true;
                            }
                        }
                    }
                }

                prevCandle = currCandle;
            }

            return new ScanResult(symbol, configLabel, trades, wins, cumPnL);
        }
    }
}
