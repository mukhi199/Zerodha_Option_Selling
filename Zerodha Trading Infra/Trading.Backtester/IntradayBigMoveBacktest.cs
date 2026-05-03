using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;

namespace Trading.Backtester
{
    public class IntradayBigMoveBacktest
    {
        public static void Run()
        {
            Console.WriteLine("\n=== INTRADAY BIG MOVE - WIN RATE FILTER COMPARISON ===");

            var rootFolder = "/Users/Lenovo/Projects/Zerodha_Option_Selling";
            var nifty50File = Path.Combine(rootFolder, "NIFTY 50_5minute.csv");
            var bankNiftyFile = Path.Combine(rootFolder, "NIFTY BANK_5minute.csv");

            // Run under different filter combinations
            var configs = new[] 
            {
                (Label: "Baseline (1.2x, 9:45-14:30, no extras)",         TimeStart: 9*60+45, TimeEnd: 14*60+30, VolumeFilter: false, PullbackFilter: false),
                (Label: "Time Filter (10:30-12:30)",                       TimeStart: 10*60+30, TimeEnd: 12*60+30, VolumeFilter: false, PullbackFilter: false),
                (Label: "Volume Spike Filter",                             TimeStart: 9*60+45, TimeEnd: 14*60+30, VolumeFilter: true,  PullbackFilter: false),
                (Label: "Pullback Entry Filter",                           TimeStart: 9*60+45, TimeEnd: 14*60+30, VolumeFilter: false, PullbackFilter: true),
                (Label: "ALL Filters Combined",                            TimeStart: 10*60+30, TimeEnd: 12*60+30, VolumeFilter: true,  PullbackFilter: true),
            };

            Console.WriteLine("\n--- NIFTY 50 (Target: +100pts) ---");
            foreach (var c in configs)
            {
                var trades = RunSymbol("NIFTY 50", nifty50File, 100m, 1.2m, c.TimeStart, c.TimeEnd, c.VolumeFilter, c.PullbackFilter);
                PrintResult(c.Label, trades);
            }

            Console.WriteLine("\n--- NIFTY BANK (Target: +200pts) ---");
            foreach (var c in configs)
            {
                var trades = RunSymbol("NIFTY BANK", bankNiftyFile, 200m, 1.2m, c.TimeStart, c.TimeEnd, c.VolumeFilter, c.PullbackFilter);
                PrintResult(c.Label, trades);
            }

            Console.WriteLine("\n✅ Filter Comparison Complete!\n");
        }

        static void PrintResult(string label, List<TradeRecord> trades)
        {
            int total = trades.Count;
            int winners = trades.Count(x => x.PnL > 0);
            double wr = total > 0 ? ((double)winners / total) * 100.0 : 0;
            decimal pnl = trades.Sum(x => x.PnL);
            Console.WriteLine($"  [{label,-45}] Trades: {total,-4} | WinRate: {wr,5:F1}% | PnL: {pnl,9:F2}");
        }

        static List<TradeRecord> RunSymbol(string symbol, string filePath, decimal targetLockPoints, decimal anomalyThreshold,
            int windowStartMin, int windowEndMin, bool useVolumeFilter, bool usePullbackFilter)
        {
            var trades = new List<TradeRecord>();
            if (!File.Exists(filePath)) return trades;

            Console.WriteLine($"[Loading Data] {filePath}");

            int totalTrades = 0, winningTrades = 0, losingTrades = 0;
            decimal cumulativePoints = 0;

            bool isActive = false, isLong = false, isTrailingEma = false;
            decimal entryPrice = 0, slPrice = 0;
            DateTime entryTime = DateTime.MinValue;
            decimal maxBodyToday = 0;
            DateTime currentDay = DateTime.MinValue;
            
            decimal ema9 = 0;
            decimal ema50 = 0;
            bool emaReady = false;

            // Volume tracking
            decimal _volumeAccum = 0;
            int _volumeCount = 0;

            // Pullback filter state: after detecting anomaly, wait for pullback
            bool waitingForPullback = false;
            bool pullbackIsLong = false;
            decimal pullbackAnomalyBody = 0;
            decimal pullbackSl = 0;
            decimal pullbackOpen = 0;
            decimal pullbackClose = 0;

            using (var reader = new StreamReader(filePath))
            {
                reader.ReadLine(); // Header
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var parts = line.Split(',');
                    if (parts.Length < 5) continue;

                    if (!DateTime.TryParse(parts[0], out DateTime candleTime)) continue;
                    if (!decimal.TryParse(parts[1], out decimal open)) continue;
                    if (!decimal.TryParse(parts[2], out decimal high)) continue;
                    if (!decimal.TryParse(parts[3], out decimal low)) continue;
                    if (!decimal.TryParse(parts[4], out decimal close)) continue;
                    decimal volume = parts.Length > 5 && decimal.TryParse(parts[5], out decimal v) ? v : 0m;

                    var tod = candleTime.TimeOfDay;
                    int todMins = tod.Hours * 60 + tod.Minutes;
                    decimal body = Math.Abs(close - open);

                    // EMA Updates
                    decimal alpha9 = 2m / (9m + 1m);
                    decimal alpha50 = 2m / (50m + 1m);
                    if (!emaReady) { ema9 = close; ema50 = close; emaReady = true; }
                    else { 
                        ema9 = alpha9 * close + (1 - alpha9) * ema9; 
                        ema50 = alpha50 * close + (1 - alpha50) * ema50;
                    }

                    // Track rolling average volume for the day (for volume spike filter)
                    // We approximate average volume per day by accumulating volume each candle
                    decimal avgVolumeToday = 0;
                    if (candleTime.Date > currentDay.Date)
                    {
                        currentDay = candleTime.Date;
                        maxBodyToday = 0;
                        _volumeAccum = 0;
                        _volumeCount = 0;
                        if (isActive) { isActive = false; }
                    }

                    _volumeAccum += volume;
                    _volumeCount++;
                    avgVolumeToday = _volumeCount > 0 ? _volumeAccum / _volumeCount : 0;

                    if (isActive)
                    {
                        // Check stop loss first
                        bool hitSl = isLong ? low <= slPrice : high >= slPrice;
                        bool eodSqOff = tod >= new TimeSpan(15, 15, 0);
                        
                        if (!isTrailingEma)
                        {
                            decimal pnlOpen = isLong ? high - entryPrice : entryPrice - low;
                            if (pnlOpen >= targetLockPoints)
                            {
                                isTrailingEma = true;
                            }
                        }

                        // Trailing exit check
                        bool trailExit = isTrailingEma && (isLong ? close < ema9 : close > ema9);

                        if (hitSl || eodSqOff || trailExit)
                        {
                            string reason = hitSl ? "Initial SL" : (eodSqOff ? "EOD" : "Trailing EMA9");
                            decimal exitP = hitSl ? slPrice : close;

                            decimal pnl = isLong ? exitP - entryPrice : entryPrice - exitP;
                            cumulativePoints += pnl; totalTrades++;
                            if (pnl > 0) winningTrades++; else losingTrades++;

                            trades.Add(new TradeRecord
                            {
                                Symbol = symbol,
                                Type = isLong ? "Long" : "Short",
                                Strategy = "AnomalyBigMove",
                                EntryTime = entryTime,
                                EntryPrice = entryPrice,
                                ExitTime = candleTime,
                                ExitPrice = exitP,
                                PnL = pnl,
                                CumulativePnL = cumulativePoints,
                                ExitReason = reason
                            });

                            isActive = false;
                            isTrailingEma = false;
                        }
                    }
                    else
                    {
                        // Evaluate entry
                        bool inWindow = todMins >= windowStartMin && todMins < windowEndMin;

                        // Pullback Filter: if we detected an anomaly last candle, wait for pullback into body
                        if (usePullbackFilter && waitingForPullback)
                        {
                            bool pulledBack = pullbackIsLong
                                ? (low <= pullbackOpen + pullbackAnomalyBody * 0.5m)  // price dipped back halfway into bull candle
                                : (high >= pullbackOpen - pullbackAnomalyBody * 0.5m); // price bounced halfway into bear candle

                            if (pulledBack && inWindow)
                            {
                                // Confirm pullback didn't break past anomaly candle low (for longs)
                                bool confirmOk = pullbackIsLong ? (low > pullbackSl) : (high < pullbackSl);
                                if (confirmOk)
                                {
                                    isActive = true;
                                    isLong = pullbackIsLong;
                                    entryPrice = close;
                                    slPrice = pullbackSl;
                                    entryTime = candleTime;
                                    isTrailingEma = false;
                                }
                            }
                            waitingForPullback = false;
                        }
                        else if (!isActive && inWindow && maxBodyToday > 0 && body > maxBodyToday * anomalyThreshold)
                        {
                            bool isBullish = close > open;
                            bool trendAlignmentOk = isBullish ? (open > ema50) : (open < ema50);
                            bool volOk = !useVolumeFilter || (volume > 0 && avgVolumeToday > 0 && volume >= avgVolumeToday * 1.5m);

                            if (trendAlignmentOk && volOk)
                            {
                                if (usePullbackFilter)
                                {
                                    // Mark the anomaly and wait for pullback entry next candle
                                    waitingForPullback = true;
                                    pullbackIsLong = isBullish;
                                    pullbackAnomalyBody = body;
                                    pullbackSl = isBullish ? low : high;
                                    pullbackOpen = open;
                                    pullbackClose = close;
                                }
                                else
                                {
                                    isActive = true;
                                    isLong = isBullish;
                                    entryPrice = close;
                                    slPrice = isBullish ? low : high;
                                    entryTime = candleTime;
                                    isTrailingEma = false;
                                }
                            }
                        }
                    }

                    // Finally update the daily max body size (AFTER entry evaluation) so we don't compare the candle against itself
                    if (tod >= new TimeSpan(9, 45, 0))
                    {
                        maxBodyToday = Math.Max(maxBodyToday, body);
                    }
                }
            }
            return trades;
        }

        static void WriteTrades(XLWorkbook workbook, string sheetName, List<TradeRecord> trades)
        {
            var ws = workbook.Worksheets.Add(sheetName);
            ws.Cell(1, 1).Value = "Symbol";
            ws.Cell(1, 2).Value = "Type";
            ws.Cell(1, 3).Value = "Entry Time";
            ws.Cell(1, 4).Value = "Entry Price";
            ws.Cell(1, 5).Value = "Exit Time";
            ws.Cell(1, 6).Value = "Exit Price";
            ws.Cell(1, 7).Value = "PnL";
            ws.Cell(1, 8).Value = "Cumulative PnL";
            ws.Cell(1, 9).Value = "Exit Reason";

            var header = ws.Range(1, 1, 1, 9);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.LightGray;

            int row = 2;
            foreach (var t in trades)
            {
                ws.Cell(row, 1).Value = t.Symbol;
                ws.Cell(row, 2).Value = t.Type;
                ws.Cell(row, 3).Value = t.EntryTime.ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(row, 4).Value = t.EntryPrice;
                ws.Cell(row, 5).Value = t.ExitTime.ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(row, 6).Value = t.ExitPrice;
                ws.Cell(row, 7).Value = t.PnL;
                
                var pnlCell = ws.Cell(row, 7);
                if (t.PnL > 0) pnlCell.Style.Font.FontColor = XLColor.Green;
                else if (t.PnL < 0) pnlCell.Style.Font.FontColor = XLColor.Red;

                ws.Cell(row, 8).Value = t.CumulativePnL;
                ws.Cell(row, 9).Value = t.ExitReason;
                row++;
            }
            ws.Columns().AdjustToContents();
        }

        static void WriteSummary(XLWorkbook workbook, string sheetName, List<TradeRecord> trades)
        {
            var ws = workbook.Worksheets.Add(sheetName);
            ws.Cell(1, 1).Value = "Year";
            ws.Cell(1, 2).Value = "Total PnL (Points)";
            ws.Cell(1, 3).Value = "Total Trades";
            ws.Cell(1, 4).Value = "Win Rate (%)";

            var header = ws.Range(1, 1, 1, 4);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.LightYellow;

            var yearlyGroups = trades.GroupBy(t => t.EntryTime.Year).OrderBy(g => g.Key);

            Console.WriteLine($"\n--- Summary for {sheetName} ---");
            decimal totalCombinedPnl = 0;
            int totalCombinedTrades = 0;

            int row = 2;
            foreach (var group in yearlyGroups)
            {
                int year = group.Key;
                decimal yearlyPnL = group.Sum(t => t.PnL);
                int count = group.Count();
                int winners = group.Count(t => t.PnL > 0);
                double winrate = count > 0 ? ((double)winners / count) * 100.0 : 0;

                Console.WriteLine($"Year {year}: {count} Trades | Win Rate: {winrate:F2}% | Target PnL: {yearlyPnL:F2}");
                totalCombinedPnl += yearlyPnL;
                totalCombinedTrades += count;

                ws.Cell(row, 1).Value = year;
                ws.Cell(row, 2).Value = yearlyPnL;
                
                var pnlCell = ws.Cell(row, 2);
                if (yearlyPnL > 0) pnlCell.Style.Font.FontColor = XLColor.Green;
                else if (yearlyPnL < 0) pnlCell.Style.Font.FontColor = XLColor.Red;

                ws.Cell(row, 3).Value = count;
                ws.Cell(row, 4).Value = Math.Round(winrate, 2);
                row++;
            }
            Console.WriteLine($"> GRAND TOTAL {sheetName}: {totalCombinedPnl:F2} Points across {totalCombinedTrades} Trades");
            ws.Columns().AdjustToContents();
        }
    }
}
