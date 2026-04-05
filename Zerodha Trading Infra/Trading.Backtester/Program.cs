using System;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using Trading.Strategy.Services;
using Trading.Core.Models;
using Trading.Core.Utils;
using ClosedXML.Excel;

namespace Trading.Backtester
{
    public class TradeRecord
    {
        public string Symbol { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Strategy { get; set; } = string.Empty;
        public string BreakoutPattern { get; set; } = string.Empty;
        public DateTime EntryTime { get; set; }
        public decimal EntryPrice { get; set; }
        public DateTime ExitTime { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal PnL { get; set; }
        public decimal CumulativePnL { get; set; }
        public string ExitReason { get; set; } = string.Empty;
    }

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Starting Hybrid Pattern Extraction Backtest ===");

            var rootFolder = "/Users/Lenovo/Projects/Zerodha_Option_Selling";
            var nifty50File = Path.Combine(rootFolder, "NIFTY 50_5minute.csv");
            var bankNiftyFile = Path.Combine(rootFolder, "NIFTY BANK_5minute.csv");

            var nifty50Trades = RunBacktestForSymbol("NIFTY 50", nifty50File);
            Console.WriteLine("\n----------------------------------------------------\n");
            var bankNiftyTrades = RunBacktestForSymbol("NIFTY BANK", bankNiftyFile);

            var exportPath = Path.Combine(rootFolder, "Backtest_Results_HybridORB_FixedSL.xlsx");
            using (var workbook = new XLWorkbook())
            {
                WriteTradesToWorksheet(workbook, "NIFTY 50", nifty50Trades);
                WriteTradesToWorksheet(workbook, "NIFTY BANK", bankNiftyTrades);
                WriteYearlySummaryToWorksheet(workbook, "Yearly NIFTY 50", nifty50Trades);
                WriteYearlySummaryToWorksheet(workbook, "Yearly NIFTY BANK", bankNiftyTrades);
                workbook.SaveAs(exportPath);
            }

            Console.WriteLine($"\n=== Backtest Complete! ===");
            Console.WriteLine($"Hybrid Pattern Engine Workbook saved to: {exportPath}");

            // Run the PDLH strategy as a separate backtest
            PrevDayLastHourBacktest.Run();

            // Run the combined PDLH + 3-Day engine
            CombinedBreakoutBacktest.Run();

            // Run the deep parameter scanner
            StrategyScannerBacktest.Run();
        }

        static void WriteTradesToWorksheet(XLWorkbook workbook, string sheetName, List<TradeRecord> trades)
        {
            var ws = workbook.Worksheets.Add(sheetName);
            ws.Cell(1, 1).Value = "Symbol";
            ws.Cell(1, 2).Value = "Type";
            ws.Cell(1, 3).Value = "Strategy";
            ws.Cell(1, 4).Value = "Breakout Pattern";
            ws.Cell(1, 5).Value = "Entry Time";
            ws.Cell(1, 6).Value = "Entry Price";
            ws.Cell(1, 7).Value = "Exit Time";
            ws.Cell(1, 8).Value = "Exit Price";
            ws.Cell(1, 9).Value = "PnL";
            ws.Cell(1, 10).Value = "Cumulative PnL";
            ws.Cell(1, 11).Value = "Exit Reason";

            var header = ws.Range(1, 1, 1, 11);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.LightGray;

            int row = 2;
            foreach (var t in trades)
            {
                ws.Cell(row, 1).Value = t.Symbol;
                ws.Cell(row, 2).Value = t.Type;
                ws.Cell(row, 3).Value = t.Strategy;
                ws.Cell(row, 4).Value = t.BreakoutPattern;
                ws.Cell(row, 5).Value = t.EntryTime.ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(row, 6).Value = t.EntryPrice;
                ws.Cell(row, 7).Value = t.ExitTime.ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(row, 8).Value = t.ExitPrice;
                ws.Cell(row, 9).Value = t.PnL;
                
                var pnlCell = ws.Cell(row, 9);
                if (t.PnL > 0) pnlCell.Style.Font.FontColor = XLColor.Green;
                else if (t.PnL < 0) pnlCell.Style.Font.FontColor = XLColor.Red;

                ws.Cell(row, 10).Value = t.CumulativePnL;
                ws.Cell(row, 11).Value = t.ExitReason;
                row++;
            }
            ws.Columns().AdjustToContents();
        }

        static void WriteYearlySummaryToWorksheet(XLWorkbook workbook, string sheetName, List<TradeRecord> trades)
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

            int row = 2;
            foreach (var group in yearlyGroups)
            {
                int year = group.Key;
                decimal yearlyPnL = group.Sum(t => t.PnL);
                int count = group.Count();
                int winners = group.Count(t => t.PnL > 0);
                double winrate = count > 0 ? ((double)winners / count) * 100.0 : 0;

                ws.Cell(row, 1).Value = year;
                ws.Cell(row, 2).Value = yearlyPnL;
                
                var pnlCell = ws.Cell(row, 2);
                if (yearlyPnL > 0) pnlCell.Style.Font.FontColor = XLColor.Green;
                else if (yearlyPnL < 0) pnlCell.Style.Font.FontColor = XLColor.Red;

                ws.Cell(row, 3).Value = count;
                ws.Cell(row, 4).Value = Math.Round(winrate, 2);
                row++;
            }
            ws.Columns().AdjustToContents();
        }

        static List<TradeRecord> RunBacktestForSymbol(string symbol, string filePath)
        {
            var activeTrades = new List<TradeRecord>();

            Console.WriteLine($"[Loading Data] {filePath}");
            if (!File.Exists(filePath)) return activeTrades;

            var tracker = new TechnicalIndicatorsTracker();
            
            int totalTrades = 0, winningTrades = 0, losingTrades = 0;
            decimal cumulativePoints = 0;

            bool isLong = false, isShort = false;
            decimal entryPrice = 0, slPrice = 0;
            string entryStrategy = "";
            string entryPattern = "";
            DateTime entryTime = DateTime.MinValue;

            decimal stopLossMarginLimit = symbol == "NIFTY BANK" ? 150m : 60m;

            DateTime currentDay = DateTime.MinValue;
            decimal currentDayHigh = decimal.MinValue, currentDayLow = decimal.MaxValue;
            bool tradedToday = false;

            decimal orbHigh = decimal.MinValue, orbLow = decimal.MaxValue;
            bool brokeOrbLow = false, brokeOrbHigh = false;

            Candle? prevCandle = null;

            using (var reader = new StreamReader(filePath))
            {
                reader.ReadLine(); 
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var parts = line.Split(',');
                    if (parts.Length < 5) continue;

                    var dateStr = parts[0];
                    if (!DateTime.TryParse(dateStr, out DateTime candleTime)) continue;
                    if (!decimal.TryParse(parts[1], out decimal openPrice)) continue;
                    if (!decimal.TryParse(parts[2], out decimal highPrice)) continue;
                    if (!decimal.TryParse(parts[3], out decimal lowPrice)) continue;
                    if (!decimal.TryParse(parts[4], out decimal closePrice)) continue;

                    var timeOfDay = candleTime.TimeOfDay;

                    Candle currCandle = new Candle 
                    { 
                        StartTime = candleTime, 
                        Open = openPrice, 
                        High = highPrice, 
                        Low = lowPrice, 
                        Close = closePrice 
                    };

                    if (currentDay == DateTime.MinValue || candleTime.Date > currentDay.Date)
                    {
                        if (currentDay != DateTime.MinValue) 
                        {
                            tracker.AddDailyRange(symbol, currentDayHigh, currentDayLow);
                        }
                        
                        currentDay = candleTime.Date;
                        currentDayHigh = decimal.MinValue;
                        currentDayLow = decimal.MaxValue;
                        tradedToday = false; 

                        orbHigh = decimal.MinValue;
                        orbLow = decimal.MaxValue;
                        brokeOrbLow = false;
                        brokeOrbHigh = false;
                        prevCandle = null;
                    }

                    currentDayHigh = Math.Max(currentDayHigh, highPrice);
                    currentDayLow = Math.Min(currentDayLow, lowPrice);

                    if (timeOfDay <= new TimeSpan(10, 15, 0))
                    {
                        orbHigh = Math.Max(orbHigh, highPrice);
                        orbLow = Math.Min(orbLow, lowPrice);
                    }

                    var range = tracker.GetThreeDayRange(symbol);

                    // --- EXITS ---
                    if (isLong)
                    {
                        bool hitSL = lowPrice <= slPrice;
                        bool eodSqOff = timeOfDay >= new TimeSpan(15, 15, 0);

                        if (hitSL || eodSqOff)
                        {
                            string reason = hitSL ? "StopLoss(Fixed)" : "EOD SquareOff";
                            decimal exitP = hitSL ? slPrice : closePrice;
                            
                            decimal pnl = exitP - entryPrice;
                            cumulativePoints += pnl; totalTrades++;
                            if (pnl > 0) winningTrades++; else losingTrades++;

                            activeTrades.Add(new TradeRecord {
                                Symbol = symbol, Type = "Long", Strategy = entryStrategy, BreakoutPattern = entryPattern,
                                EntryTime = entryTime, EntryPrice = entryPrice, ExitTime = candleTime, 
                                ExitPrice = exitP, PnL = pnl, CumulativePnL = cumulativePoints, ExitReason = reason
                            });
                            
                            isLong = false;
                        }
                    }
                    else if (isShort)
                    {
                        bool hitSL = highPrice >= slPrice;
                        bool eodSqOff = timeOfDay >= new TimeSpan(15, 15, 0);

                        if (hitSL || eodSqOff)
                        {
                            string reason = hitSL ? "StopLoss(Fixed)" : "EOD SquareOff";
                            decimal exitP = hitSL ? slPrice : closePrice;
                            
                            decimal pnl = entryPrice - exitP;
                            cumulativePoints += pnl; totalTrades++;
                            if (pnl > 0) winningTrades++; else losingTrades++;

                            activeTrades.Add(new TradeRecord {
                                Symbol = symbol, Type = "Short", Strategy = entryStrategy, BreakoutPattern = entryPattern,
                                EntryTime = entryTime, EntryPrice = entryPrice, ExitTime = candleTime, 
                                ExitPrice = exitP, PnL = pnl, CumulativePnL = cumulativePoints, ExitReason = reason
                            });
                            
                            isShort = false;
                        }
                    }

                    // --- ENTRIES (1 Per Day Max) ---
                    if (!isLong && !isShort && !tradedToday)
                    {
                        if (timeOfDay > new TimeSpan(9, 15, 0) && timeOfDay <= new TimeSpan(15, 0, 0))
                        {
                            bool validOrbTime = timeOfDay > new TimeSpan(10, 15, 0);

                            bool isBullish = closePrice > openPrice;
                            bool isBearish = closePrice < openPrice;

                            bool threeDayLong = range != null && highPrice >= range.Value.High && isBullish;
                            bool threeDayShort = range != null && lowPrice <= range.Value.Low && isBearish;

                            bool orbReversalLong = validOrbTime && brokeOrbLow && highPrice >= orbHigh && isBullish;
                            bool orbReversalShort = validOrbTime && brokeOrbHigh && lowPrice <= orbLow && isBearish;

                            if (threeDayLong || orbReversalLong)
                            {
                                // Detect Pattern
                                var p1 = CandlePatternDetector.DetectSingleCandle(currCandle);
                                var p2 = prevCandle != null ? CandlePatternDetector.DetectTwoCandle(prevCandle, currCandle) : CandlePatternDetector.CandlePattern.None;
                                var finalP = p2 != CandlePatternDetector.CandlePattern.None ? p2 : p1;
                                
                                isLong = true;
                                entryPrice = threeDayLong ? Math.Max(openPrice, range.Value.High) : Math.Max(openPrice, orbHigh);
                                entryStrategy = threeDayLong ? "3-Day Breakout" : "ORB Reversal";
                                entryPattern = CandlePatternDetector.Describe(finalP);
                                slPrice = entryPrice - stopLossMarginLimit;
                                entryTime = candleTime;
                                tradedToday = true;
                            }
                            else if (threeDayShort || orbReversalShort)
                            {
                                var p1 = CandlePatternDetector.DetectSingleCandle(currCandle);
                                var p2 = prevCandle != null ? CandlePatternDetector.DetectTwoCandle(prevCandle, currCandle) : CandlePatternDetector.CandlePattern.None;
                                var finalP = p2 != CandlePatternDetector.CandlePattern.None ? p2 : p1;

                                isShort = true;
                                entryPrice = threeDayShort ? Math.Min(openPrice, range.Value.Low) : Math.Min(openPrice, orbLow);
                                entryStrategy = threeDayShort ? "3-Day Breakout" : "ORB Reversal";
                                entryPattern = CandlePatternDetector.Describe(finalP);
                                slPrice = entryPrice + stopLossMarginLimit;
                                entryTime = candleTime;
                                tradedToday = true;
                            }
                        }
                    }

                    // --- UPDATE STATE FOR NEXT CANDLE ---
                    if (timeOfDay > new TimeSpan(10, 15, 0))
                    {
                        if (lowPrice < orbLow) brokeOrbLow = true;
                        if (highPrice > orbHigh) brokeOrbHigh = true;
                    }
                    
                    prevCandle = currCandle;
                }
            }

            Console.WriteLine($"=== Pattern Analytics Engine Report for {symbol} ===");
            Console.WriteLine($"Total Trades:      {totalTrades}");
            Console.WriteLine($"Winning Trades:    {winningTrades}");
            Console.WriteLine($"Losing Trades:     {losingTrades}");
            if (totalTrades > 0)
                Console.WriteLine($"Win Rate:          {((double)winningTrades / totalTrades * 100):F2}%");
            Console.WriteLine($"Cumulative Profit: {cumulativePoints:F2} Points");

            Console.WriteLine("\n--- Breakout Pattern Performance ---");
            var patternGroups = activeTrades.GroupBy(t => t.BreakoutPattern)
                .OrderByDescending(g => g.Count() > 0 ? (double)g.Count(x => x.PnL > 0) / g.Count() : 0);
            
            foreach(var g in patternGroups)
            {
                int pCount = g.Count();
                int pWins = g.Count(t => t.PnL > 0);
                double pWR = pCount > 0 ? (double)pWins / pCount * 100 : 0;
                decimal pPnL = g.Sum(t => t.PnL);
                Console.WriteLine($"{g.Key,-20} | Trades: {pCount,4} | WinRate: {pWR,5:F1}% | PnL: {pPnL,8:F2}");
            }
            Console.WriteLine("------------------------------------\n");

            return activeTrades;
        }
    }
}
