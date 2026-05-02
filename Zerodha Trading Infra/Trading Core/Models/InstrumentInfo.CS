namespace Trading.Core.Models;

/// <summary>
/// Instrument metadata loaded from KDB+ or fetched from Kite API.
/// </summary>
public class InstrumentInfo
{
    public uint InstrumentToken { get; set; }

    public string TradingSymbol { get; set; } = string.Empty;

    public string Exchange { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public decimal TickSize { get; set; }

    public int LotSize { get; set; }

    public string InstrumentType { get; set; } = string.Empty;

    public string Segment { get; set; } = string.Empty;

    public System.DateTime? Expiry { get; set; }
}
