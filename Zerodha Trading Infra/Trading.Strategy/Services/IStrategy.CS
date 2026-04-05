namespace Trading.Strategy.Services;

using Trading.Core.Models;

public interface IStrategy
{
    void OnTick(NormalizedTick tick);
    void OnCandle(Candle candle);
}
