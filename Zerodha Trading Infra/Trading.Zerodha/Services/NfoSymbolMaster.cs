using System;
using System.Collections.Generic;
using System.Linq;
using KiteConnect;
using Microsoft.Extensions.Logging;

namespace Trading.Zerodha.Services
{
    public interface INfoSymbolMaster
    {
        void Initialize(string accessToken);
        (string TradingSymbol, int LotSize) GetActiveFuture(string spotSymbol);
        (string TradingSymbol, int LotSize) GetActiveOtmOption(string spotSymbol, decimal currentPrice, bool isPut);
        (string TradingSymbol, int LotSize) GetActiveStrikeOption(string spotSymbol, decimal strike, bool isPut);
    }

    public class NfoSymbolMaster : INfoSymbolMaster
    {
        private readonly string _apiKey;
        private readonly ILogger<NfoSymbolMaster> _logger;
        private List<Instrument> _nfoInstruments = new();
        private DateTime _lastFetch = DateTime.MinValue;
        private readonly System.Threading.ManualResetEventSlim _initializedEvent = new(false);

        public NfoSymbolMaster(string apiKey, ILogger<NfoSymbolMaster> logger)
        {
            _apiKey = apiKey;
            _logger = logger;
        }

        public void Initialize(string accessToken)
        {
            if (_nfoInstruments.Count > 0 && (DateTime.Now - _lastFetch).TotalHours < 12)
                return; // Already fetched today

            try
            {
                var kite = new Kite(_apiKey, accessToken);
                _logger.LogInformation("Downloading NFO Instrument Master from Zerodha Kite...");
                var allNfo = kite.GetInstruments("NFO");

                // Filter only Nifty and BankNifty to save memory
                _nfoInstruments = allNfo.Where(i => 
                    i.Name == "NIFTY" || i.Name == "BANKNIFTY" || i.Name == "NIFTY50" || i.Name == "NIFTY 50"
                ).ToList();

                if (_nfoInstruments.Count == 0 && allNfo.Count > 0)
                {
                    var sampleNames = allNfo.Take(10).Select(i => i.Name).Distinct().ToList();
                    _logger.LogWarning("NFO Filter resulted in 0 instruments. Raw samples: {Samples}", string.Join(", ", sampleNames));
                }

                _lastFetch = DateTime.Now;
                _logger.LogInformation("Successfully cached {Count} NFO derivative instruments.", _nfoInstruments.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download NFO instruments from Zerodha.");
            }
            finally
            {
                _initializedEvent.Set(); // Signal ready even if failed, to unblock threads
            }
        }

        public (string TradingSymbol, int LotSize) GetActiveFuture(string spotSymbol)
        {
            _initializedEvent.Wait(); // Block until initialized
            string baseSymbol = spotSymbol == "NIFTY 50" ? "NIFTY" : "BANKNIFTY";
            
            // Find the closest future expiry
            var future = _nfoInstruments
                .Where(i => i.Name == baseSymbol && i.InstrumentType == "FUT" && i.Expiry >= DateTime.UtcNow.Date)
                .OrderBy(i => i.Expiry)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(future.TradingSymbol))
                return (future.TradingSymbol, (int)future.LotSize);
            
            // Fallback default
            return ($"{baseSymbol}24MAYFUT", spotSymbol == "NIFTY 50" ? 25 : 15);
        }

        public (string TradingSymbol, int LotSize) GetActiveOtmOption(string spotSymbol, decimal currentPrice, bool isPut)
        {
            _initializedEvent.Wait(); // Block until initialized
            string baseSymbol = spotSymbol == "NIFTY 50" ? "NIFTY" : "BANKNIFTY";
            string instrType = isPut ? "PE" : "CE";
            
            // Strike roughly 5% out of the money
            decimal targetStrike = isPut ? currentPrice * 0.95m : currentPrice * 1.05m;
            
            // Find the closest expiry that has options (usually weekly)
            var currentExpiryOptions = _nfoInstruments
                .Where(i => i.Name == baseSymbol && i.InstrumentType == instrType && i.Expiry >= DateTime.UtcNow.Date)
                .OrderBy(i => i.Expiry)
                .ToList();

            if (currentExpiryOptions.Any())
            {
                var closestExpiry = currentExpiryOptions.First().Expiry;
                
                // Find the option in that expiry closest to our target strike
                var option = currentExpiryOptions
                    .Where(i => i.Expiry == closestExpiry)
                    .OrderBy(i => Math.Abs(i.Strike - targetStrike))
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(option.TradingSymbol))
                    return (option.TradingSymbol, (int)option.LotSize);
            }

            // Fallback default
            return ($"{baseSymbol}24MAY{(int)(targetStrike/100)*100}{instrType}", spotSymbol == "NIFTY 50" ? 25 : 15);
        }
        public (string TradingSymbol, int LotSize) GetActiveStrikeOption(string spotSymbol, decimal strike, bool isPut)
        {
            _initializedEvent.Wait(); // Block until initialized
            string baseSymbol = spotSymbol == "NIFTY 50" ? "NIFTY" : "BANKNIFTY";
            string instrType = isPut ? "PE" : "CE";
            
            _logger.LogInformation("Searching for {Symbol} {Type} options. Total Master Count: {TotalCount}", baseSymbol, instrType, _nfoInstruments.Count);
            
            var currentExpiryOptions = _nfoInstruments
                .Where(i => i.Name == baseSymbol && i.InstrumentType == instrType && i.Expiry.HasValue && i.Expiry.Value.Date >= DateTime.UtcNow.Date)
                .OrderBy(i => i.Expiry)
                .ToList();

            if (!currentExpiryOptions.Any())
            {
                var sample = _nfoInstruments.Where(i => i.Name == baseSymbol).Take(5).Select(i => $"{i.TradingSymbol} ({i.Expiry:yyyy-MM-dd})").ToList();
                _logger.LogWarning("No {Symbol} {Type} options found for today! Found sample: {Sample}", baseSymbol, instrType, string.Join(", ", sample));
            }

            if (currentExpiryOptions.Any())
            {
                var closestExpiry = currentExpiryOptions.First().Expiry;
                
                // Find the option in that expiry closest to the requested strike
                var option = currentExpiryOptions
                    .Where(i => i.Expiry == closestExpiry)
                    .OrderBy(i => Math.Abs(i.Strike - strike))
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(option.TradingSymbol))
                    return (option.TradingSymbol, (int)option.LotSize);
            }

            return (string.Empty, 0);
        }
    }
}
