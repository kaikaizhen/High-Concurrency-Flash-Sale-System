namespace FlashSale.Api.Models.ViewModels.Diagnostics;

public class MetricsViewModel
{
    public long DbCommands { get; set; }

    public long CacheHits { get; set; }

    public long CacheMisses { get; set; }

    public long CacheErrors { get; set; }

    public double CacheHitRate { get; set; }
}
