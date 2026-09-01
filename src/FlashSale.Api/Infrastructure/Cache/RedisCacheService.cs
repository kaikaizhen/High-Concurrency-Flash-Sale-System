using System.Text.Json;
using FlashSale.Api.Infrastructure.Diagnostics;
using FlashSale.Api.Infrastructure.Observability;
using FlashSale.Api.Options;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FlashSale.Api.Infrastructure.Cache;

public class RedisCacheService : ICacheService
{
    /// <summary>
    /// 負向快取的哨兵值。
    ///
    /// 不能直接存 JSON 的 <c>null</c>，因為那與「反序列化失敗」難以區分。
    /// 用一個不可能出現在正常 payload 的字串明確表達「這個 Key 查無資料」。
    ///
    /// 正常 payload 一定是序列化後的 JSON 物件（以 <c>{</c> 開頭），
    /// 不可能與這個值相撞，因此不需要用控制字元當前綴 ——
    /// 在原始碼裡寫入字面的 NUL 位元組會讓整個檔案被 Git 判定為二進位檔而無法 diff。
    /// </summary>
    private const string NullSentinel = "__null__";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConnectionMultiplexer _connection;
    private readonly IMetricsCollector _metrics;
    private readonly FlashSaleMetrics _flashSaleMetrics;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly string _prefix;

    public RedisCacheService(
        IConnectionMultiplexer connection,
        IMetricsCollector metrics,
        FlashSaleMetrics flashSaleMetrics,
        IOptions<RedisOptions> redisOptions,
        ILogger<RedisCacheService> logger)
    {
        _connection = connection;
        _metrics = metrics;
        _flashSaleMetrics = flashSaleMetrics;
        _logger = logger;
        _prefix = redisOptions.Value.InstanceName;
    }

    public async Task<CacheResult<T>> GetAsync<T>(string key)
    {
        try
        {
            var value = await _connection
                .GetDatabase()
                .StringGetAsync(_prefix + key);

            if (!value.HasValue)
            {
                _metrics.RecordCacheMiss();
                _flashSaleMetrics.RecordCacheLookup(hit: false);
                _logger.LogDebug("Cache MISS. Key={Key}", key);

                return CacheResult<T>.Miss();
            }

            _metrics.RecordCacheHit();
            _flashSaleMetrics.RecordCacheLookup(hit: true);
            _logger.LogDebug("Cache HIT. Key={Key}", key);

            var raw = value.ToString();

            if (raw == NullSentinel)
            {
                return CacheResult<T>.Hit(default);
            }

            return CacheResult<T>.Hit(
                JsonSerializer.Deserialize<T>(raw, SerializerOptions));
        }
        catch (Exception ex)
        {
            // 快取故障不應該讓整個請求失敗，退化成直接查資料庫即可。
            // 但必須記錄下來 —— 靜默地失去快取，看起來就只是「突然變慢」。
            _metrics.RecordCacheError();
            _logger.LogError(ex, "Cache GET failed. Key={Key}", key);

            return CacheResult<T>.Miss();
        }
    }

    public async Task SetAsync<T>(string key, T? value, TimeSpan ttl)
    {
        try
        {
            var payload = value is null
                ? NullSentinel
                : JsonSerializer.Serialize(value, SerializerOptions);

            await _connection
                .GetDatabase()
                .StringSetAsync(_prefix + key, payload, ttl);
        }
        catch (Exception ex)
        {
            _metrics.RecordCacheError();
            _logger.LogError(ex, "Cache SET failed. Key={Key}", key);
        }
    }

    public async Task RemoveAsync(string key)
    {
        try
        {
            await _connection
                .GetDatabase()
                .KeyDeleteAsync(_prefix + key);

            _logger.LogDebug("Cache INVALIDATE. Key={Key}", key);
        }
        catch (Exception ex)
        {
            // 清除失敗比讀取失敗嚴重：快取會留著過期資料直到 TTL 到期。
            _metrics.RecordCacheError();
            _logger.LogError(ex, "Cache REMOVE failed. Key={Key}", key);
        }
    }
}
