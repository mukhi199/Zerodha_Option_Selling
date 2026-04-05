namespace Trading.Core.Data;

using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext
{
    public DbSet<CprData>                CprData               { get; set; }
    public DbSet<DailyClose>             DailyCloses           { get; set; }
    public DbSet<MovingAverageSnapshot>  MovingAverageSnapshots { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // CPR: unique per symbol + timeframe + date
        modelBuilder.Entity<CprData>()
            .HasIndex(c => new { c.Symbol, c.Timeframe, c.Date })
            .IsUnique();

        // DailyClose: one row per symbol per date
        modelBuilder.Entity<DailyClose>()
            .HasIndex(d => new { d.Symbol, d.Date })
            .IsUnique();

        // MA Snapshot: one row per symbol per date
        modelBuilder.Entity<MovingAverageSnapshot>()
            .HasIndex(m => new { m.Symbol, m.Date })
            .IsUnique();
    }
}

