using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Trading.Core.Models;
using Trading.Core.Utils;
using ClosedXML.Excel;

namespace Trading.Backtester
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Shared result record
    // ═══════════════════════════════════════════════════════════════════════════
    public record StrategyResult(
        string StrategyName,
        string Symbol,
        int    Trades,
        int    Wins,
        int    Losses,
        decimal GrossPoints,
        List<TradeRecord> Trades_ // raw trades for Excel
    )
    {
        public double WinRate  => Trades > 0 ? (double)Wins / Trades * 100 : 0;
        public double LossRate => Trades > 0 ? (double)Losses / Trades * 100 : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Strategy 1 — VWAP Band Mean-Reversion Scalp
    // Entry: Price > VWAP + k*StdDev → Short | Price < VWAP - k*StdDev → Long
    // Exit : Price reverts to VWAP midline OR SL
    // ═══════════════════════════════════════════════════════════════════════════
    public static class VwapReversionStrategy
    {
        const decimal BandMultiplier = 1.5m;   // StdDev multiplier
        const decimal TpNifty        = 50m;    // points TP
        const decimal TpBank         = 120m;
        const decimal SlNifty        = 80m;
        const decimal SlBank         = 200m;

        public static StrategyResult Run(string symbol, string filePath)
        {
            decimal tp = symbol == "NIFTY BANK" ? TpBank : TpNifty;
            decimal sl = symbol == "NIFTY BANK" ? SlBank : SlNifty;
            var trades = new List<TradeRecord>();
            int wins = 0, losses = 0; decimal cumPnL = 0;

            bool isLong = false, isShort = false;
            decimal entryPrice = 0, slPrice = 0, tpPrice = 0;
            DateTime entryTime = DateTime.MinValue;

            DateTime currentDay  = DateTime.MinValue;
            bool tradedToday = false;

            // VWAP state (reset daily)
            decimal sumPV = 0, sumVol = 0;
            decimal sumSqDev = 0;
            int      candleCount = 0;
            decimal vwap = 0;

            var lastHourEnd = new TimeSpan(15, 15, 0);

            using var reader = new StreamReader(filePath);
            reader.ReadLine();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var p = line.Split(',');
                if (p.Length < 6) continue;
                if (!DateTime.TryParse(p[0], out DateTime ct))  continue;
                if (!decimal.TryParse(p[1], out decimal open))  continue;
                if (!decimal.TryParse(p[2], out decimal high))  continue;
                if (!decimal.TryParse(p[3], out decimal low))   continue;
                if (!decimal.TryParse(p[4], out decimal close)) continue;
                if (!decimal.TryParse(p[5], out decimal vol))   vol = 1m;
                if (vol <= 0) vol = 1m; // Index CSV has 0 volume, simulate time-weighted VWAP

                var tod = ct.TimeOfDay;

                if (currentDay == DateTime.MinValue || ct.Date > currentDay.Date)
                {
                    currentDay   = ct.Date;
                    tradedToday  = false;
                    sumPV = 0; sumVol = 0; sumSqDev = 0; candleCount = 0; vwap = 0;
                }

                decimal typicalPrice = (high + low + close) / 3m;
                sumPV  += typicalPrice * vol;
                sumVol += vol;
                vwap    = sumVol > 0 ? sumPV / sumVol : typicalPrice;
                decimal dev = Math.Abs(typicalPrice - vwap);
                sumSqDev += dev * dev;
                candleCount++;
                decimal stdDev = candleCount > 1 ? (decimal)Math.Sqrt((double)(sumSqDev / candleCount)) : 0;

                // Exit
                if (isLong || isShort)
                {
                    bool hitSL = isLong ? low  <= slPrice : high >= slPrice;
                    bool hitTP = isLong ? high >= tpPrice : low  <= tpPrice;
                    bool eod   = tod >= lastHourEnd;
                    if (hitSL || hitTP || eod)
                    {
                        decimal exitP = hitSL ? slPrice : hitTP ? tpPrice : close;
                        decimal pnl   = isLong ? exitP - entryPrice : entryPrice - exitP;
                        cumPnL += pnl;
                        if (pnl > 0) wins++; else losses++;
                        trades.Add(new TradeRecord { Symbol = symbol, Type = isLong ? "Long" : "Short",
                            Strategy = "VWAP Reversion", BreakoutPattern = "VWAP Band",
                            EntryTime = entryTime, EntryPrice = entryPrice,
                            ExitTime = ct, ExitPrice = exitP, PnL = pnl, CumulativePnL = cumPnL,
                            ExitReason = hitSL ? "SL" : hitTP ? "TP" : "EOD" });
                        isLong = isShort = false;
                    }
                }

                if (!isLong && !isShort && !tradedToday && stdDev > 0)
                {
                    bool inWindow = tod >= new TimeSpan(9, 30, 0) && tod < new TimeSpan(14, 30, 0);
                    if (inWindow)
                    {
                        decimal upperBand = vwap + BandMultiplier * stdDev;
                        decimal lowerBand = vwap - BandMultiplier * stdDev;

                        if (close > upperBand && close < open) // Bearish candle hitting upper band
                        {
                            isShort = true; entryPrice = close;
                            slPrice = close + sl; tpPrice = vwap;
                            entryTime = ct; tradedToday = true;
                        }
                        else if (close < lowerBand && close > open) // Bullish candle hitting lower band
                        {
                            isLong = true; entryPrice = close;
                            slPrice = close - sl; tpPrice = vwap;
                            entryTime = ct; tradedToday = true;
                        }
                    }
                }
            }

            int total = wins + losses;
            return new StrategyResult("VWAP Reversion", symbol, total, wins, losses, cumPnL, trades);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Strategy 2 — 15-Min ORB (First 3 candles Opening Range Breakout)
    // Entry: Break of first 3-candle high/low with a momentum candle
    // Exit : Fixed TP = 1.5× range width | SL = other side of range
    // ═══════════════════════════════════════════════════════════════════════════
    public static class OrbFifteenMinStrategy
    {
        public static StrategyResult Run(string symbol, string filePath)
        {
            decimal slFrac = 1.0m;  // SL = full ORB width
            decimal tpFrac = 1.5m;  // TP = 1.5× ORB width
            var trades = new List<TradeRecord>();
            int wins = 0, losses = 0; decimal cumPnL = 0;

            bool isLong = false, isShort = false;
            decimal entryPrice = 0, slPrice = 0, tpPrice = 0;
            DateTime entryTime = DateTime.MinValue;

            DateTime currentDay = DateTime.MinValue;
            bool tradedToday = false;
            int candlesToday = 0;
            decimal orbHigh = decimal.MinValue, orbLow = decimal.MaxValue;
            bool orbSet = false;

            var orbEnd     = new TimeSpan(9, 45, 0);  // after 3 candles
            var lastHourEnd = new TimeSpan(15, 15, 0);

            using var reader = new StreamReader(filePath);
            reader.ReadLine();
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

                if (currentDay == DateTime.MinValue || ct.Date > currentDay.Date)
                {
                    currentDay   = ct.Date;
                    tradedToday  = false;
                    candlesToday = 0;
                    orbHigh = decimal.MinValue; orbLow = decimal.MaxValue;
                    orbSet  = false;
                }

                // Build ORB (first 3 candles)
                if (tod <= orbEnd)
                {
                    candlesToday++;
                    orbHigh = Math.Max(orbHigh, high);
                    orbLow  = Math.Min(orbLow,  low);
                    if (candlesToday >= 3) orbSet = true;
                }

                // Exit
                if (isLong || isShort)
                {
                    bool hitSL = isLong ? low  <= slPrice : high >= slPrice;
                    bool hitTP = isLong ? high >= tpPrice : low  <= tpPrice;
                    bool eod   = tod >= lastHourEnd;
                    if (hitSL || hitTP || eod)
                    {
                        decimal exitP = hitSL ? slPrice : hitTP ? tpPrice : close;
                        decimal pnl   = isLong ? exitP - entryPrice : entryPrice - exitP;
                        cumPnL += pnl;
                        if (pnl > 0) wins++; else losses++;
                        trades.Add(new TradeRecord { Symbol = symbol, Type = isLong ? "Long" : "Short",
                            Strategy = "15-Min ORB", BreakoutPattern = "ORB Break",
                            EntryTime = entryTime, EntryPrice = entryPrice,
                            ExitTime = ct, ExitPrice = exitP, PnL = pnl, CumulativePnL = cumPnL,
                            ExitReason = hitSL ? "SL" : hitTP ? "TP" : "EOD" });
                        isLong = isShort = false;
                    }
                }

                if (!isLong && !isShort && !tradedToday && orbSet)
                {
                    decimal orbWidth = orbHigh - orbLow;
                    if (orbWidth <= 0) { continue; }

                    bool inWindow = tod > orbEnd && tod < new TimeSpan(12, 0, 0);
                    if (inWindow)
                    {
                        bool breakUp   = high >= orbHigh && close > open;
                        bool breakDown = low  <= orbLow  && close < open;

                        if (breakUp)
                        {
                            isLong = true; entryPrice = Math.Max(open, orbHigh);
                            slPrice = entryPrice - orbWidth * slFrac;
                            tpPrice = entryPrice + orbWidth * tpFrac;
                            entryTime = ct; tradedToday = true;
                        }
                        else if (breakDown)
                        {
                            isShort = true; entryPrice = Math.Min(open, orbLow);
                            slPrice = entryPrice + orbWidth * slFrac;
                            tpPrice = entryPrice - orbWidth * tpFrac;
                            entryTime = ct; tradedToday = true;
                        }
                    }
                }
            }

            int total = wins + losses;
            return new StrategyResult("15-Min ORB", symbol, total, wins, losses, cumPnL, trades);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Strategy 3 — Narrow CPR Breakout
    // CPR is narrow if (R - S) < 0.15% of pivot. Breakout = crossing TC or BC with Marubozu.
    // TP = 2× CPR width | SL = opposite CPR boundary
    // ═══════════════════════════════════════════════════════════════════════════
    public static class NarrowCprBreakoutStrategy
    {
        const decimal NarrowThresholdPct = 0.0015m;  // 0.15% of price

        public static StrategyResult Run(string symbol, string filePath)
        {
            var trades = new List<TradeRecord>();
            int wins = 0, losses = 0; decimal cumPnL = 0;

            bool isLong = false, isShort = false;
            decimal entryPrice = 0, slPrice = 0, tpPrice = 0;
            DateTime entryTime = DateTime.MinValue;

            DateTime currentDay  = DateTime.MinValue;
            bool tradedToday = false;
            decimal pivot = 0, tc = 0, bc = 0, prevH = 0, prevL = 0, prevC = 0;
            bool cprReady = false;
            decimal dayH = decimal.MinValue, dayL = decimal.MaxValue, dayC = 0;

            var lastHourEnd = new TimeSpan(15, 15, 0);

            using var reader = new StreamReader(filePath);
            reader.ReadLine();
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

                if (currentDay == DateTime.MinValue || ct.Date > currentDay.Date)
                {
                    // Compute CPR for the new day from previous day's data
                    if (currentDay != DateTime.MinValue && prevH > 0)
                    {
                        pivot  = (prevH + prevL + prevC) / 3m;
                        tc     = (prevH + prevL) / 2m;
                        bc     = pivot - (tc - pivot);
                        decimal cprWidth = Math.Abs(tc - bc);
                        cprReady = pivot > 0 && (cprWidth / pivot) < NarrowThresholdPct;
                    }
                    prevH = dayH; prevL = dayL; prevC = dayC;
                    currentDay  = ct.Date;
                    tradedToday = false;
                    dayH = decimal.MinValue; dayL = decimal.MaxValue; dayC = 0;
                }

                dayH = Math.Max(dayH, high);
                dayL = Math.Min(dayL,  low);
                dayC = close;

                // Exit
                if (isLong || isShort)
                {
                    bool hitSL = isLong ? low  <= slPrice : high >= slPrice;
                    bool hitTP = isLong ? high >= tpPrice : low  <= tpPrice;
                    bool eod   = tod >= lastHourEnd;
                    if (hitSL || hitTP || eod)
                    {
                        decimal exitP = hitSL ? slPrice : hitTP ? tpPrice : close;
                        decimal pnl   = isLong ? exitP - entryPrice : entryPrice - exitP;
                        cumPnL += pnl;
                        if (pnl > 0) wins++; else losses++;
                        trades.Add(new TradeRecord { Symbol = symbol, Type = isLong ? "Long" : "Short",
                            Strategy = "Narrow CPR Breakout", BreakoutPattern = "CPR Narrow",
                            EntryTime = entryTime, EntryPrice = entryPrice,
                            ExitTime = ct, ExitPrice = exitP, PnL = pnl, CumulativePnL = cumPnL,
                            ExitReason = hitSL ? "SL" : hitTP ? "TP" : "EOD" });
                        isLong = isShort = false;
                    }
                }

                if (!isLong && !isShort && !tradedToday && cprReady)
                {
                    bool inWindow = tod >= new TimeSpan(9, 20, 0) && tod < new TimeSpan(13, 0, 0);
                    if (inWindow && pivot > 0)
                    {
                        decimal cprWidth = Math.Abs(tc - bc);
                        decimal tpSize   = cprWidth * 2m;
                        var p1 = CandlePatternDetector.DetectSingleCandle(
                                    new Candle { Open=open, High=high, Low=low, Close=close, StartTime=ct });
                        bool marubozu = p1 == CandlePatternDetector.CandlePattern.BullishMarubozu ||
                                        p1 == CandlePatternDetector.CandlePattern.BearishMarubozu;

                        if (close > Math.Max(tc, bc) && close > open && marubozu)
                        {
                            isLong = true; entryPrice = close;
                            slPrice = Math.Min(tc, bc);
                            tpPrice = entryPrice + tpSize;
                            entryTime = ct; tradedToday = true;
                        }
                        else if (close < Math.Min(tc, bc) && close < open && marubozu)
                        {
                            isShort = true; entryPrice = close;
                            slPrice = Math.Max(tc, bc);
                            tpPrice = entryPrice - tpSize;
                            entryTime = ct; tradedToday = true;
                        }
                    }
                }
            }

            int total = wins + losses;
            return new StrategyResult("Narrow CPR Breakout", symbol, total, wins, losses, cumPnL, trades);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Strategy 4 — Inside Bar + EMA21 Breakout
    // Entry: Inside Bar whose high/low breaks with a directional candle aligned with EMA21 trend
    // TP = 2× inside bar height | SL = opposite end of inside bar
    // ═══════════════════════════════════════════════════════════════════════════
    public static class InsideBarEma21Strategy
    {
        const int EmaPeriod = 21;

        public static StrategyResult Run(string symbol, string filePath)
        {
            var trades = new List<TradeRecord>();
            int wins = 0, losses = 0; decimal cumPnL = 0;

            bool isLong = false, isShort = false;
            decimal entryPrice = 0, slPrice = 0, tpPrice = 0;
            DateTime entryTime = DateTime.MinValue;
            bool tradedToday = false;
            DateTime currentDay = DateTime.MinValue;

            decimal emaAlpha = 2m / (EmaPeriod + 1);
            decimal emaValue = 0;
            bool emaReady = false;

            Candle? prevCandle = null;

            var lastHourEnd = new TimeSpan(15, 15, 0);

            using var reader = new StreamReader(filePath);
            reader.ReadLine();
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

                // EMA update
                if (!emaReady) { emaValue = close; emaReady = true; }
                else           { emaValue = emaAlpha * close + (1 - emaAlpha) * emaValue; }

                var tod = ct.TimeOfDay;
                if (currentDay == DateTime.MinValue || ct.Date > currentDay.Date)
                {
                    currentDay  = ct.Date;
                    tradedToday = false;
                    prevCandle  = null;
                }

                var curr = new Candle { StartTime = ct, Open = open, High = high, Low = low, Close = close };

                // Exit
                if (isLong || isShort)
                {
                    bool hitSL = isLong ? low  <= slPrice : high >= slPrice;
                    bool hitTP = isLong ? high >= tpPrice : low  <= tpPrice;
                    bool eod   = tod >= lastHourEnd;
                    if (hitSL || hitTP || eod)
                    {
                        decimal exitP = hitSL ? slPrice : hitTP ? tpPrice : close;
                        decimal pnl   = isLong ? exitP - entryPrice : entryPrice - exitP;
                        cumPnL += pnl;
                        if (pnl > 0) wins++; else losses++;
                        trades.Add(new TradeRecord { Symbol = symbol, Type = isLong ? "Long" : "Short",
                            Strategy = "Inside Bar + EMA21", BreakoutPattern = "Inside Bar",
                            EntryTime = entryTime, EntryPrice = entryPrice,
                            ExitTime = ct, ExitPrice = exitP, PnL = pnl, CumulativePnL = cumPnL,
                            ExitReason = hitSL ? "SL" : hitTP ? "TP" : "EOD" });
                        isLong = isShort = false;
                    }
                }

                if (!isLong && !isShort && !tradedToday && prevCandle != null && emaReady)
                {
                    bool inWindow   = tod >= new TimeSpan(9, 30, 0) && tod < new TimeSpan(13, 0, 0);
                    bool isInsideBar = high <= prevCandle.High && low >= prevCandle.Low;

                    if (inWindow && !isInsideBar) // current bar breaks out of previous inside bar
                    {
                        // Check if PREVIOUS bar was an inside bar relative to the bar before it
                        // Here we check if prevCandle was narrow (proxy for inside bar setup)
                        decimal ibHeight = prevCandle.High - prevCandle.Low;
                        if (ibHeight > 0)
                        {
                            bool aboveEma  = close > emaValue;
                            bool belowEma  = close < emaValue;
                            bool breakUp   = high > prevCandle.High && close > open && aboveEma;
                            bool breakDown = low  < prevCandle.Low  && close < open && belowEma;

                            if (breakUp)
                            {
                                isLong = true; entryPrice = Math.Max(open, prevCandle.High);
                                slPrice = prevCandle.Low;
                                tpPrice = entryPrice + ibHeight * 2m;
                                entryTime = ct; tradedToday = true;
                            }
                            else if (breakDown)
                            {
                                isShort = true; entryPrice = Math.Min(open, prevCandle.Low);
                                slPrice = prevCandle.High;
                                tpPrice = entryPrice - ibHeight * 2m;
                                entryTime = ct; tradedToday = true;
                            }
                        }
                    }
                }

                prevCandle = curr;
            }

            int total = wins + losses;
            return new StrategyResult("Inside Bar + EMA21", symbol, total, wins, losses, cumPnL, trades);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Strategy 5 — Gap Reversal ORB
    // Gap Up -> ORB Candle is Bearish -> Break ORB Low -> Short till EOD
    // Gap Down -> ORB Candle is Bullish -> Break ORB High -> Long till EOD
    // ═══════════════════════════════════════════════════════════════════════════
    public static class GapReversalOrbStrategy
    {
        public static StrategyResult Run(string symbol, string filePath)
        {
            var trades = new List<TradeRecord>();
            int wins = 0, losses = 0; decimal cumPnL = 0;

            bool isLong = false, isShort = false;
            decimal entryPrice = 0, slPrice = 0, tpPrice = 0;
            DateTime entryTime = DateTime.MinValue;

            DateTime currentDay = DateTime.MinValue;
            bool tradedToday = false;
            int candlesToday = 0;

            decimal orbHigh = decimal.MinValue, orbLow = decimal.MaxValue;
            decimal orbOpen = 0, orbClose = 0;

            decimal dayH = decimal.MinValue, dayL = decimal.MaxValue, dayC = 0;
            decimal prevH = 0, prevL = 0, prevC = 0;
            
            bool orbSet = false;
            bool isGapUp = false, isGapDown = false;
            bool orbIsBearish = false, orbIsBullish = false;

            var orbEnd = new TimeSpan(9, 45, 0); // first 3 candles (15 mins)
            var lastHourEnd = new TimeSpan(15, 15, 0);

            using var reader = new StreamReader(filePath);
            reader.ReadLine();
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

                if (currentDay == DateTime.MinValue || ct.Date > currentDay.Date)
                {
                    if (currentDay != DateTime.MinValue)
                    {
                        prevH = dayH; prevL = dayL; prevC = dayC;
                    }
                    currentDay  = ct.Date;
                    tradedToday = false;
                    candlesToday = 0;
                    orbHigh = decimal.MinValue; orbLow = decimal.MaxValue;
                    orbSet  = false;
                    dayH = decimal.MinValue; dayL = decimal.MaxValue; dayC = 0;

                    // Gap logic: True Gap Up > Prev High, True Gap Down < Prev Low
                    // We check the open of the 9:15 candle right away.
                    isGapUp = prevH > 0 && open > prevH;
                    isGapDown = prevL > 0 && open < prevL;
                    orbOpen = open; // 15-min open is 9:15 open
                }

                dayH = Math.Max(dayH, high);
                dayL = Math.Min(dayL, low);
                dayC = close;

                // Build 15-min ORB
                if (tod <= orbEnd)
                {
                    candlesToday++;
                    orbHigh = Math.Max(orbHigh, high);
                    orbLow  = Math.Min(orbLow,  low);
                    
                    if (candlesToday == 3) // 9:25 candle completes the 15-min ORB
                    {
                        orbClose = close;
                        orbSet = true;
                        orbIsBearish = orbClose < orbOpen;
                        orbIsBullish = orbClose > orbOpen;
                    }
                }

                // Exit logic: Reversal to EOD, so we just wait for 15:15 or SL
                if (isLong || isShort)
                {
                    bool hitSL = isLong ? low <= slPrice : high >= slPrice;
                    bool eod   = tod >= lastHourEnd;

                    if (hitSL || eod)
                    {
                        decimal exitP = hitSL ? slPrice : close;
                        decimal pnl   = isLong ? exitP - entryPrice : entryPrice - exitP;
                        cumPnL += pnl;
                        if (pnl > 0) wins++; else losses++;
                        
                        trades.Add(new TradeRecord { Symbol = symbol, Type = isLong ? "Long" : "Short",
                            Strategy = "Gap Reversal ORB", BreakoutPattern = "Gap ORB",
                            EntryTime = entryTime, EntryPrice = entryPrice,
                            ExitTime = ct, ExitPrice = exitP, PnL = pnl, CumulativePnL = cumPnL,
                            ExitReason = hitSL ? "SL" : "EOD" });
                        
                        isLong = isShort = false;
                    }
                }

                // Entry logic
                if (!isLong && !isShort && !tradedToday && orbSet)
                {
                    // Look for trade before 12:00 PM for intraday reversals
                    bool inWindow = tod > orbEnd && tod < new TimeSpan(12, 0, 0);
                    if (inWindow)
                    {
                        // Gap Up Reversal (Short)
                        if (isGapUp && orbIsBearish)
                        {
                            bool breaksLow = low <= orbLow && close < open; // breakout candle should be bearish
                            if (breaksLow)
                            {
                                isShort = true; entryPrice = Math.Min(open, orbLow);
                                slPrice = orbHigh; // SL is Day High (ORB top)
                                entryTime = ct; tradedToday = true;
                            }
                        }
                        // Gap Down Reversal (Long)
                        else if (isGapDown && orbIsBullish)
                        {
                            bool breaksHigh = high >= orbHigh && close > open; // breakout candle should be bullish
                            if (breaksHigh)
                            {
                                isLong = true; entryPrice = Math.Max(open, orbHigh);
                                slPrice = orbLow; // SL is Day Low (ORB bot)
                                entryTime = ct; tradedToday = true;
                            }
                        }
                    }
                }
            }

            int total = wins + losses;
            return new StrategyResult("Gap Reversal ORB", symbol, total, wins, losses, cumPnL, trades);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Multi-Strategy Runner — Runs all 4 + Combined, prints table, writes Excel
    // ═══════════════════════════════════════════════════════════════════════════
    public static class MultiStrategyRunner
    {
        static readonly string Root      = "/Users/Lenovo/Projects/Zerodha_Option_Selling";
        static readonly string NiftyFile = "/Users/Lenovo/Projects/Zerodha_Option_Selling/NIFTY 50_5minute.csv";
        static readonly string BankFile  = "/Users/Lenovo/Projects/Zerodha_Option_Selling/NIFTY BANK_5minute.csv";

        public static void Run()
        {
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║        MULTI-STRATEGY GRAND COMPARISON BACKTEST              ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝\n");
            Console.WriteLine("Running all 4 strategies. Please wait...\n");

            var results = new List<StrategyResult>();

            // ── Strategy 1: VWAP Reversion ────────────────────────────────────
            results.Add(VwapReversionStrategy.Run("NIFTY 50",   NiftyFile));
            results.Add(VwapReversionStrategy.Run("NIFTY BANK", BankFile));

            // ── Strategy 2: 15-Min ORB ────────────────────────────────────────
            results.Add(OrbFifteenMinStrategy.Run("NIFTY 50",   NiftyFile));
            results.Add(OrbFifteenMinStrategy.Run("NIFTY BANK", BankFile));

            // ── Strategy 3: Narrow CPR Breakout ──────────────────────────────
            results.Add(NarrowCprBreakoutStrategy.Run("NIFTY 50",   NiftyFile));
            results.Add(NarrowCprBreakoutStrategy.Run("NIFTY BANK", BankFile));

            // ── Strategy 4: Inside Bar + EMA21 ───────────────────────────────
            results.Add(InsideBarEma21Strategy.Run("NIFTY 50",   NiftyFile));
            results.Add(InsideBarEma21Strategy.Run("NIFTY BANK", BankFile));

            // ── Strategy 5: Gap Reversal ORB ─────────────────────────────────
            results.Add(GapReversalOrbStrategy.Run("NIFTY 50",   NiftyFile));
            results.Add(GapReversalOrbStrategy.Run("NIFTY BANK", BankFile));

            // ── Print Tabular Summary ─────────────────────────────────────────
            PrintTable(results);

            // ── Excel Export ──────────────────────────────────────────────────
            var exportPath = Path.Combine(Root, "Backtest_Results_MultiStrategy.xlsx");
            ExportExcel(results, exportPath);

            Console.WriteLine($"\nExcel saved: {exportPath}");
        }

        static void PrintTable(List<StrategyResult> results)
        {
            Console.WriteLine($"{"Strategy",-24} | {"Symbol",-11} | {"Trades",6} | {"Wins",5} | {"Losses",6} | {"Win Rate",9} | {"Points",12}");
            Console.WriteLine(new string('─', 100));

            foreach (var r in results)
            {
                string wr   = $"{r.WinRate:F1}%";
                string pts  = $"{r.GrossPoints:F2}";
                string wrDisplay = r.WinRate >= 60 ? $"★ {wr}" : wr;
                Console.WriteLine($"{r.StrategyName,-24} | {r.Symbol,-11} | {r.Trades,6} | {r.Wins,5} | {r.Losses,6} | {wrDisplay,9} | {pts,12}");
            }

            Console.WriteLine(new string('─', 100));

            // Per-strategy totals
            Console.WriteLine("\n── Combined (Nifty + BankNifty) per Strategy ──");
            Console.WriteLine($"{"Strategy",-24} | {"Total Trades",12} | {"Combined Win%",14} | {"Combined Points",15}");
            Console.WriteLine(new string('─', 75));
            foreach (var g in results.GroupBy(r => r.StrategyName))
            {
                int total  = g.Sum(r => r.Trades);
                int wins   = g.Sum(r => r.Wins);
                decimal pts = g.Sum(r => r.GrossPoints);
                double wr  = total > 0 ? (double)wins / total * 100 : 0;
                Console.WriteLine($"{g.Key,-24} | {total,12} | {wr,13:F1}% | {pts,15:F2}");
            }
        }

        static void ExportExcel(List<StrategyResult> results, string path)
        {
            using var wb = new XLWorkbook();

            // ── Summary comparison sheet ──────────────────────────────────────
            var summary = wb.Worksheets.Add("Strategy Comparison");
            string[] sh = { "Strategy", "Symbol", "Total Trades", "Wins", "Losses", "Win Rate (%)", "Gross Points" };
            for (int i = 0; i < sh.Length; i++) summary.Cell(1, i + 1).Value = sh[i];
            summary.Range(1, 1, 1, sh.Length).Style.Font.Bold = true;
            summary.Range(1, 1, 1, sh.Length).Style.Fill.BackgroundColor = XLColor.DarkBlue;
            summary.Range(1, 1, 1, sh.Length).Style.Font.FontColor = XLColor.White;

            int sr = 2;
            foreach (var r in results)
            {
                summary.Cell(sr, 1).Value = r.StrategyName;
                summary.Cell(sr, 2).Value = r.Symbol;
                summary.Cell(sr, 3).Value = r.Trades;
                summary.Cell(sr, 4).Value = r.Wins;
                summary.Cell(sr, 5).Value = r.Losses;
                summary.Cell(sr, 6).Value = Math.Round(r.WinRate, 2);
                summary.Cell(sr, 7).Value = r.GrossPoints;

                if (r.WinRate >= 60)
                    summary.Row(sr).Style.Fill.BackgroundColor = XLColor.LightGreen;
                else if (r.GrossPoints > 0)
                    summary.Row(sr).Style.Fill.BackgroundColor = XLColor.LightYellow;
                else
                    summary.Row(sr).Style.Fill.BackgroundColor = XLColor.LightCoral;
                sr++;
            }
            summary.Columns().AdjustToContents();

            // ── Individual trade sheets per strategy ──────────────────────────
            foreach (var r in results)
            {
                string sheetName = $"{r.StrategyName.Replace("+","").Replace(" ","")}_{r.Symbol.Replace(" ","_")}";
                if (sheetName.Length > 31) sheetName = sheetName[..31];
                var ws = wb.Worksheets.Add(sheetName);

                string[] h = { "Type","Entry Time","Entry Price","Exit Time","Exit Price","PnL","Cumulative PnL","Exit Reason" };
                for (int i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
                ws.Range(1, 1, 1, h.Length).Style.Font.Bold = true;
                ws.Range(1, 1, 1, h.Length).Style.Fill.BackgroundColor = XLColor.LightGray;

                int row = 2;
                foreach (var t in r.Trades_)
                {
                    ws.Cell(row, 1).Value = t.Type;
                    ws.Cell(row, 2).Value = t.EntryTime.ToString("yyyy-MM-dd HH:mm");
                    ws.Cell(row, 3).Value = t.EntryPrice;
                    ws.Cell(row, 4).Value = t.ExitTime.ToString("yyyy-MM-dd HH:mm");
                    ws.Cell(row, 5).Value = t.ExitPrice;
                    ws.Cell(row, 6).Value = t.PnL;
                    ws.Cell(row, 6).Style.Font.FontColor = t.PnL > 0 ? XLColor.Green : XLColor.Red;
                    ws.Cell(row, 7).Value = t.CumulativePnL;
                    ws.Cell(row, 8).Value = t.ExitReason;
                    row++;
                }
                ws.Columns().AdjustToContents();
            }

            wb.SaveAs(path);
        }
    }
}
