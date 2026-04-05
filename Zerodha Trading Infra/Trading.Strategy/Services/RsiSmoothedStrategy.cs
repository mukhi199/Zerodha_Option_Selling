namespace Trading.Strategy.Services;

using Microsoft.Extensions.Logging;
using Trading.Core.Models;
using System.Collections.Generic;

public class RsiSmoothedStrategy : IStrategy
{
    private readonly OrderExecutionService _orderService;
    private readonly ILogger<RsiSmoothedStrategy> _logger;
    private readonly TechnicalIndicatorsTracker _tracker;

    // Track active positions per symbol
    private readonly Dictionary<string, bool> _isLong = new();
    private readonly Dictionary<string, bool> _isShort = new();
    private readonly Dictionary<string, decimal> _entryPrice = new();
    private readonly Dictionary<string, decimal> _slPrice = new();
    private readonly Dictionary<string, decimal> _tpPrice = new();

    public RsiSmoothedStrategy(OrderExecutionService orderService, TechnicalIndicatorsTracker tracker, ILogger<RsiSmoothedStrategy> logger)
    {
        _orderService = orderService;
        _tracker = tracker;
        _logger = logger;
    }

    private bool IsLong(string symbol) => _isLong.GetValueOrDefault(symbol, false);
    private bool IsShort(string symbol) => _isShort.GetValueOrDefault(symbol, false);

    public void OnTick(NormalizedTick tick)
    {
        // Not used for this strategy
    }

    public void OnCandle(Candle candle)
    {
        if (candle.IntervalMinutes == 15 && (candle.Symbol == "NIFTY 50" || candle.Symbol == "NIFTY BANK"))
        {
            var rsiData = _tracker.GetRsiAndRma(candle.Symbol);

            if (rsiData == null) return;

            var rsi = rsiData.Value.Rsi;
            var prevRsi = rsiData.Value.PrevRsi;
            var rma = rsiData.Value.Rma;
            var prevRma = rsiData.Value.PrevRma;

            var timeStart = new TimeSpan(9, 15, 0); // Open all day
            var timeEnd = new TimeSpan(15, 0, 0);
            var timeSquareOff = new TimeSpan(15, 15, 0);

            var timeOfDay = candle.StartTime.TimeOfDay;

            // Cross checks
            bool crossedUnder = prevRsi > prevRma && rsi <= rma;
            bool crossedOver = prevRsi < prevRma && rsi >= rma;

            // Signals based on Pine Script logic
            bool buySignal = prevRsi < 20 && prevRma < 20 && crossedOver;
            bool sellSignal = prevRsi > 80 && crossedUnder;

            bool isLong = IsLong(candle.Symbol);
            bool isShort = IsShort(candle.Symbol);
            decimal entryPrice = _entryPrice.GetValueOrDefault(candle.Symbol, 0);
            decimal slPrice = _slPrice.GetValueOrDefault(candle.Symbol, 0);
            decimal tpPrice = _tpPrice.GetValueOrDefault(candle.Symbol, 0);

            // Execution Logic
            if (isLong) 
            {
                bool isStopLoss = candle.Close <= slPrice;
                bool isTakeProfit = candle.Close >= tpPrice;
                bool isIndicatorExit = crossedUnder;
                bool isSquareOffTime = timeOfDay >= timeSquareOff;

                if (isStopLoss || isTakeProfit || isIndicatorExit || isSquareOffTime)
                {
                    _logger.LogInformation("[RsiSmoothedStrategy] Exit Long condition met on 15m CANDLE for {Symbol} at {Close}. Reason: SL({SL}) TP({TP}) Ind({Ind}) SqOff({SqOff})", 
                        candle.Symbol, candle.Close, isStopLoss, isTakeProfit, isIndicatorExit, isSquareOffTime);
                    // _orderService.PlaceOrder(candle.Symbol, "NSE", 1, "SELL");
                    _isLong[candle.Symbol] = false;
                    isLong = false;
                }
            }

            if (isShort) 
            {
                bool isStopLoss = candle.Close >= slPrice;
                bool isTakeProfit = candle.Close <= tpPrice;
                bool isIndicatorExit = crossedOver;
                bool isSquareOffTime = timeOfDay >= timeSquareOff;

                if (isStopLoss || isTakeProfit || isIndicatorExit || isSquareOffTime)
                {
                    _logger.LogInformation("[RsiSmoothedStrategy] Exit Short condition met on 15m CANDLE for {Symbol} at {Close}. Reason: SL({SL}) TP({TP}) Ind({Ind}) SqOff({SqOff})", 
                        candle.Symbol, candle.Close, isStopLoss, isTakeProfit, isIndicatorExit, isSquareOffTime);
                    // _orderService.PlaceOrder(candle.Symbol, "NSE", 1, "BUY");
                    _isShort[candle.Symbol] = false;
                    isShort = false;
                }
            }

            if (!isLong && !isShort && timeOfDay >= timeStart && timeOfDay <= timeEnd)
            {
                if (buySignal)
                {
                    _logger.LogInformation("[RsiSmoothedStrategy] BUY SIGNAL met on 15m CANDLE for {Symbol} at {Close}. RSI: {RSI:F2}, RMA: {RMA:F2}", candle.Symbol, candle.Close, rsi, rma);
                    // _orderService.PlaceOrder(candle.Symbol, "NSE", 1, "BUY");
                    _isLong[candle.Symbol] = true;
                    _entryPrice[candle.Symbol] = candle.Close;
                    _slPrice[candle.Symbol] = candle.Low;
                    _tpPrice[candle.Symbol] = candle.Close + 100m;
                }
                else if (sellSignal)
                {
                    _logger.LogInformation("[RsiSmoothedStrategy] SELL SIGNAL met on 15m CANDLE for {Symbol} at {Close}. RSI: {RSI:F2}, RMA: {RMA:F2}", candle.Symbol, candle.Close, rsi, rma);
                    // _orderService.PlaceOrder(candle.Symbol, "NSE", 1, "SELL");
                    _isShort[candle.Symbol] = true;
                    _entryPrice[candle.Symbol] = candle.Close;
                    _slPrice[candle.Symbol] = candle.High;
                    _tpPrice[candle.Symbol] = candle.Close - 100m;
                }
            }
        }
    }
}
