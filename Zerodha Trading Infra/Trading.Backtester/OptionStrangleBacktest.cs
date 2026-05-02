using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KiteConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Trading.Core.Models;

namespace Trading.Backtester
{
    public class OptionStrangleBacktest
    {
        private readonly Kite _kite;
        private readonly ILogger<OptionStrangleBacktest> _logger;
        private readonly string _apiKey;
        private readonly string _accessToken;

        private List<Candle> _niftySpotHistory = new();

        public OptionStrangleBacktest(string apiKey, string accessToken, ILogger<OptionStrangleBacktest> logger)
        {
            _kite = new Kite(apiKey, accessToken);
            _apiKey = apiKey;
            _accessToken = accessToken;
            _logger = logger;
        }

        public void LoadSpotData(string csvPath)
        {
            _logger.LogInformation("Loading Spot Historical Data from {Path}...", csvPath);
            var lines = System.IO.File.ReadAllLines(csvPath).Skip(1);
            foreach (var line in lines)
            {
                var p = line.Split(',');
                if (p.Length >= 5 && DateTime.TryParse(p[0], out var dt))
                {
                    _niftySpotHistory.Add(new Candle {
                        StartTime = dt,
                        Open = decimal.Parse(p[1]),
                        High = decimal.Parse(p[2]),
                        Low = decimal.Parse(p[3]),
                        Close = decimal.Parse(p[4])
                    });
                }
            }
            _logger.LogInformation("Loaded {Count} minutes of Nifty Spot data.", _niftySpotHistory.Count);
        }

        public async Task<List<DailyBacktestResult>> RunAsync(DateTime fromDate, DateTime toDate)
        {
            _logger.LogInformation("Starting 1-Year 9:20 Strangle Backtest from {From} to {To}", fromDate.ToShortDateString(), toDate.ToShortDateString());

            var results = new List<DailyBacktestResult>();
            var tradingDays = GetTradingDays(fromDate, toDate);

            foreach (var day in tradingDays)
            {
                try
                {
                    _logger.LogInformation("Processing Day: {Day}", day.ToShortDateString());
                    var result = await ProcessDay(day);
                    if (result != null)
                    {
                        results.Add(result);
                        _logger.LogInformation("Result for {Day}: P&L: {PL}", day.ToShortDateString(), result.CeExitPoints + result.PeExitPoints);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process day {Day}", day.ToShortDateString());
                }

                // Rate limiting protection
                await Task.Delay(10);
            }

            Summarize(results);
            return results;
        }

        private async Task<DailyBacktestResult?> ProcessDay(DateTime day)
        {
            // 1. Get Nifty Spot at 9:20 from local memory
            var candle920 = _niftySpotHistory.FirstOrDefault(c => c.StartTime == day.Date.AddHours(9).AddMinutes(20));
            if (candle920 == null) return null;

            decimal spot = candle920.Close;
            decimal ceStrike = Math.Round((spot + 200) / 50) * 50;
            decimal peStrike = Math.Round((spot - 200) / 50) * 50;

            // 2. Resolve Expiry and Symbols
            var expiry = GetNearestExpiry(day);
            string ceSymbol = GetOptionSymbol("NIFTY", expiry, ceStrike, "CE");
            string peSymbol = GetOptionSymbol("NIFTY", expiry, peStrike, "PE");

            // 3. Load the daily option CSV
            string monthDir = day.Month.ToString();
            string fileName = $"nifty_options_{day.ToString("dd_MM_yyyy")}.csv";
            string filePath = $"/Users/Lenovo/Projects/nifty_2024_option_data/nifty_data/nifty_options/{day.Year}/{monthDir}/{fileName}";

            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("Option data file not found: {Path}", filePath);
                return null;
            }

            var ceEntry = 0m;
            var peEntry = 0m;
            var ceSlHit = false;
            var peSlHit = false;
            var ceExit = 0m;
            var peExit = 0m;
            var ceSlHitTime = "N/A";
            var peSlHitTime = "N/A";

            // 4. Parse the daily CSV correctly
            var lines = System.IO.File.ReadAllLines(filePath).Skip(1);
            var optData = lines.Select(l => {
                var p = l.Split(',');
                return new { Time = p[1], Sym = p[2], Open = decimal.Parse(p[3]), High = decimal.Parse(p[4]), Low = decimal.Parse(p[5]), Close = decimal.Parse(p[6]) };
            }).ToList();

            var ceEntries = optData.Where(x => x.Sym == ceSymbol).ToList();
            var peEntries = optData.Where(x => x.Sym == peSymbol).ToList();

            if (!ceEntries.Any() || !peEntries.Any()) 
            {
                 _logger.LogWarning("Symbols {CE} or {PE} not found in {File}", ceSymbol, peSymbol, fileName);
                 return null;
            }

            // Entry at 9:20
            var ceStart = ceEntries.FirstOrDefault(x => x.Time == "09:20:00");
            var peStart = peEntries.FirstOrDefault(x => x.Time == "09:20:00");

            if (ceStart == null || peStart == null) return null;

            ceEntry = ceStart.Open;
            peEntry = peStart.Open;
            decimal ceSlTarget = ceEntry * 2.0m;
            decimal peSlTarget = peEntry * 2.0m;

            // Monitor SL hit or EOD
            foreach (var c in ceEntries.Where(x => string.Compare(x.Time, "09:20:00") > 0))
            {
                if (c.High >= ceSlTarget) 
                { 
                    ceSlHit = true; 
                    ceExit = ceSlTarget; 
                    ceSlHitTime = c.Time;
                    break; 
                }
                ceExit = c.Close;
                if (string.Compare(c.Time, "15:15:00") >= 0) break;
            }

            foreach (var p in peEntries.Where(x => string.Compare(x.Time, "09:20:00") > 0))
            {
                if (p.High >= peSlTarget) 
                { 
                    peSlHit = true; 
                    peExit = peSlTarget; 
                    peSlHitTime = p.Time;
                    break; 
                }
                peExit = p.Close;
                if (string.Compare(p.Time, "15:15:00") >= 0) break;
            }

            return new DailyBacktestResult
            {
                Date = day,
                Spot = spot,
                CeEntry = ceEntry,
                PeEntry = peEntry,
                CeExitPoints = ceEntry - ceExit,
                PeExitPoints = peEntry - peExit,
                CeSlHit = ceSlHit,
                PeSlHit = peSlHit,
                CeSlHitTime = ceSlHitTime,
                PeSlHitTime = peSlHitTime
            };
        }

        private string GetOptionSymbol(string index, DateTime expiry, decimal strike, string type)
        {
            // Format: NIFTY04JAN2418300PE
            string day = expiry.ToString("dd");
            string month = expiry.ToString("MMM").ToUpper();
            string year = expiry.ToString("yy");
            return $"{index}{day}{month}{year}{strike}{type}";
        }
        
        private uint ResolveToken(string symbol) => 0;

        private DateTime GetNearestExpiry(DateTime day)
        {
            DateTime expiry = day.Date;
            while (expiry.DayOfWeek != DayOfWeek.Thursday) expiry = expiry.AddDays(1);
            return expiry;
        }

        private void Summarize(List<DailyBacktestResult> results)
        {
            if (results == null || !results.Any())
            {
                _logger.LogWarning("No results to summarize.");
                return;
            }

            _logger.LogInformation("--- 2024 REAL-DATA STRANGLE BACKTEST SUMMARY ---");
            _logger.LogInformation("Total Days: {Count}", results.Count);
            
            decimal totalPnL = results.Sum(r => r.CeExitPoints + r.PeExitPoints);
            _logger.LogInformation("Total Points Captured: {Sum}", totalPnL);
            _logger.LogInformation("Average Points/Day: {Avg:F1}", totalPnL / results.Count);
            _logger.LogInformation("Win Rate: {Rate:P2}", (double)results.Count(r => (r.CeExitPoints + r.PeExitPoints) > 0) / results.Count);
        }

        private List<DateTime> GetTradingDays(DateTime from, DateTime to)
        {
            var days = _niftySpotHistory
                .Where(c => c.StartTime >= from && c.StartTime <= to)
                .Select(c => c.StartTime.Date)
                .Distinct()
                .Where(d => d.DayOfWeek == DayOfWeek.Thursday) // Expiry Day Only
                .ToList();
            return days;
        }
    }

    public class DailyBacktestResult
    {
        public DateTime Date { get; set; }
        public decimal Spot { get; set; }
        public decimal CeEntry { get; set; }
        public decimal PeEntry { get; set; }
        public bool CeSlHit { get; set; }
        public bool PeSlHit { get; set; }
        public string CeSlHitTime { get; set; } = "N/A";
        public string PeSlHitTime { get; set; } = "N/A";
        public decimal CeExitPoints { get; set; }
        public decimal PeExitPoints { get; set; }
    }
}
