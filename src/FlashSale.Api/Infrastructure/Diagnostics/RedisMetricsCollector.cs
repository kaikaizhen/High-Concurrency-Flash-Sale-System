using FlashSale.Api.Middlewares;
using FlashSale.Api.Models.Dtos.Diagnostics;
using FlashSale.Api.Options;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FlashSale.Api.Infrastructure.Diagnostics;

/// <summary>
/// 共用計數器（Redis）。
///
/// 多 Instance 下唯一正確的做法：所有機器把數字累加到同一個地方，
/// 從任何一台讀到的都是全貌。
///
/// 代價是每次計數都要一次網路往返。因此這裡刻意用 fire-and-forget
/// （<see cref="CommandFlags.FireAndForget"/>）—— 觀測數據不值得
/// 讓每個資料庫查詢都多等一次 Redis 回應。少數幾次計數遺失
/// 不影響「5000 次請求打了幾次資料庫」這種量級的判斷。
/// </summary>
public class RedisMetricsCollector : IMetricsCollector
{
    private const string DbCommandsField = "dbCommands";
    private const string CacheHitsField = "cacheHits";
    private const string CacheMissesField = "cacheMisses";
    private const string CacheErrorsField = "cacheErrors";

    private readonly IConnectionMultiplexer _connection;
    private readonly ILogger<RedisMetricsCollector> _logger;
    private readonly string _key;

    public RedisMetricsCollector(
        IConnectionMultiplexer connection,
        IOptions<RedisOptions> redisOptions,
        ILogger<RedisMetricsCollector> logger)
    {
        _connection = connection;
        _logger = logger;

        // 用單一 Hash 而不是四個獨立的 Key：
        // 讀取快照時一次 HGETALL 就能拿到全部，也保證四個數字來自同一時刻。
        _key = redisOptions.Value.InstanceName + "metrics";
    }

    public void RecordDbCommand() => Increment(DbCommandsField);

    public void RecordCacheHit() => Increment(CacheHitsField);

    public void RecordCacheMiss() => Increment(CacheMissesField);

    public void RecordCacheError() => Increment(CacheErrorsField);

    private void Increment(string field)
    {
        try
        {
            _connection.GetDatabase().HashIncrement(
                _key,
                field,
                1,
                CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            // 觀測失敗絕不能影響業務流程。
            _logger.LogDebug(ex, "Metrics increment failed. Field={Field}", field);
        }
    }

    public MetricsDtoModel GetSnapshot()
    {
        var snapshot = new MetricsDtoModel
        {
            InstanceId = InstanceHeaderMiddleware.ResolveInstanceId(),
            Scope = "Redis (all instances)"
        };

        try
        {
            var entries = _connection.GetDatabase().HashGetAll(_key);

            foreach (var entry in entries)
            {
                if (!entry.Value.TryParse(out long value))
                {
                    continue;
                }

                switch (entry.Name.ToString())
                {
                    case DbCommandsField:
                        snapshot.DbCommands = value;
                        break;
                    case CacheHitsField:
                        snapshot.CacheHits = value;
                        break;
                    case CacheMissesField:
                        snapshot.CacheMisses = value;
                        break;
                    case CacheErrorsField:
                        snapshot.CacheErrors = value;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read metrics snapshot.");
        }

        return snapshot;
    }

    public void Reset()
    {
        try
        {
            // 一台機器重設，所有機器的計數一起歸零 ——
            // 這正是共用狀態該有的行為。
            _connection.GetDatabase().KeyDelete(_key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset metrics.");
        }
    }
}
