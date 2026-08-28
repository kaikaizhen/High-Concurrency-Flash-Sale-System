namespace FlashSale.Api.Models.Dtos.Diagnostics;

public class MetricsDtoModel
{
    public long DbCommands { get; set; }

    public long CacheHits { get; set; }

    public long CacheMisses { get; set; }

    public long CacheErrors { get; set; }

    public double CacheHitRate
    {
        get
        {
            var total = CacheHits + CacheMisses;

            if (total == 0)
            {
                return 0;
            }

            return (double)CacheHits / total;
        }
    }
}
