namespace Trading.Strategy.Services;

using Trading.Core.Models;

public interface IStrategy
{
    void OnTick(NormalizedTick tick);
    void OnCandle(Candle candle);

    // Override this to return a one-line status summary for Telegram reporting.
    // Format: "StrategyName | SYMBOL | Status | Trades today | PnL"
    string GetStatusDigest() => string.Empty;
}
