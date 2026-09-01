namespace FlashSale.Api.Models.Dtos.Diagnostics;

public class MetricsDtoModel
{
    /// <summary>
    /// 回報這份數字的 Instance。
    ///
    /// 多 Instance 之後，「這是誰的數字」與數字本身同樣重要 ——
    /// 行程內計數器每台機器只看得到自己的部分。
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 計數是共用的（Redis）還是行程內的（InMemory）。
    /// </summary>
    public string Scope { get; set; } = string.Empty;

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
