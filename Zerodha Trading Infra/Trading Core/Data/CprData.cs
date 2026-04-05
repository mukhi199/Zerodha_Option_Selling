namespace Trading.Core.Data;

public class CprData
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty; // "Daily", "Weekly", "Monthly"
    public DateTime Date { get; set; }

    public decimal Pivot { get; set; }
    public decimal BottomCentral { get; set; }
    public decimal TopCentral { get; set; }
    public decimal R1 { get; set; }
    public decimal S1 { get; set; }
    public decimal R2 { get; set; }
    public decimal S2 { get; set; }
    public decimal R3 { get; set; }
    public decimal S3 { get; set; }
}
