using FlashSale.Api.Middlewares;
using FlashSale.Api.Models.Dtos.Diagnostics;

namespace FlashSale.Api.Infrastructure.Diagnostics;

/// <summary>
/// 行程內計數器。
///
/// **多 Instance 下不正確** —— 每台機器只看得到自己處理的那部分請求，
/// 三台機器就要把三份數字加起來才是全貌。
/// Stage 8 保留它作為對照組，正式路徑請用 <see cref="RedisMetricsCollector"/>。
/// </summary>
public class InMemoryMetricsCollector : IMetricsCollector
{
    private long _dbCommands;
    private long _cacheHits;
    private long _cacheMisses;
    private long _cacheErrors;

    public void RecordDbCommand()
    {
        Interlocked.Increment(ref _dbCommands);
    }

    public void RecordCacheHit()
    {
        Interlocked.Increment(ref _cacheHits);
    }

    public void RecordCacheMiss()
    {
        Interlocked.Increment(ref _cacheMisses);
    }

    public void RecordCacheError()
    {
        Interlocked.Increment(ref _cacheErrors);
    }

    public MetricsDtoModel GetSnapshot()
    {
        return new MetricsDtoModel
        {
            InstanceId = InstanceHeaderMiddleware.ResolveInstanceId(),
            Scope = "InMemory (this instance only)",
            DbCommands = Interlocked.Read(ref _dbCommands),
            CacheHits = Interlocked.Read(ref _cacheHits),
            CacheMisses = Interlocked.Read(ref _cacheMisses),
            CacheErrors = Interlocked.Read(ref _cacheErrors)
        };
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _dbCommands, 0);
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
        Interlocked.Exchange(ref _cacheErrors, 0);
    }
}
