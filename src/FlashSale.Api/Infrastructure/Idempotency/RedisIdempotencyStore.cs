using System.Text.Json;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Options;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FlashSale.Api.Infrastructure.Idempotency;

/// <summary>
/// 以 Redis 實作冪等記錄。
///
/// 核心是 <c>SET key value NX EX ttl</c>：只有在 Key 不存在時才寫入，
/// 而且「檢查 + 寫入」在 Redis 內是單一原子操作。
/// 這正是併發重複防護所需要的。
/// </summary>
public class RedisIdempotencyStore : IIdempotencyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConnectionMultiplexer _connection;
    private readonly ILogger<RedisIdempotencyStore> _logger;
    private readonly string _prefix;

    public RedisIdempotencyStore(
        IConnectionMultiplexer connection,
        IOptions<RedisOptions> redisOptions,
        ILogger<RedisIdempotencyStore> logger)
    {
        _connection = connection;
        _logger = logger;
        _prefix = redisOptions.Value.InstanceName + "idem:";
    }

    public async Task<IdempotencyEntry?> TryAcquireAsync(string key, TimeSpan ttl)
    {
        var db = _connection.GetDatabase();
        var redisKey = _prefix + key;

        var pending = JsonSerializer.Serialize(
            new IdempotencyEntry { Status = IdempotencyStatus.InProgress },
            SerializerOptions);

        // When.NotExists = SET NX。回傳 true 代表我們是第一個佔用的。
        var acquired = await db.StringSetAsync(
            redisKey,
            pending,
            ttl,
            When.NotExists);

        if (acquired)
        {
            return null;
        }

        var existing = await db.StringGetAsync(redisKey);

        if (!existing.HasValue)
        {
            // 極少見：佔用失敗但隨即讀不到 —— Key 剛好在這兩個操作之間過期。
            // 當作沒有記錄處理，讓請求正常執行。
            _logger.LogWarning(
                "Idempotency key vanished between SETNX and GET. Key={Key}", key);

            return null;
        }

        return JsonSerializer.Deserialize<IdempotencyEntry>(
            existing.ToString(), SerializerOptions);
    }

    public async Task CompleteAsync(
        string key,
        int statusCode,
        string? responseBody,
        TimeSpan ttl)
    {
        var payload = JsonSerializer.Serialize(
            new IdempotencyEntry
            {
                Status = IdempotencyStatus.Completed,
                StatusCode = statusCode,
                ResponseBody = responseBody
            },
            SerializerOptions);

        // 這裡是無條件覆寫：Key 是我們佔用的，只有我們會寫。
        await _connection.GetDatabase().StringSetAsync(_prefix + key, payload, ttl);
    }

    public Task ReleaseAsync(string key)
    {
        return _connection.GetDatabase().KeyDeleteAsync(_prefix + key);
    }
}
