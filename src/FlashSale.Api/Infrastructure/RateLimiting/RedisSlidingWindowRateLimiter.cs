using FlashSale.Api.Options;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FlashSale.Api.Infrastructure.RateLimiting;

/// <summary>
/// 以 Redis Sorted Set 實作的滑動視窗限流器。
///
/// 每個請求以「當下時間戳」為 score 存進 Sorted Set，
/// 判斷時先移除視窗外的成員，再數剩下幾個。
/// 這是真正的滑動視窗 —— 沒有固定視窗的邊界爆發問題（Stage 7 §5）。
///
/// 整段邏輯必須是**一個 Lua 腳本**：
/// 「清理 → 計數 → 決定 → 寫入」拆成多次往返的話，
/// 兩個併發請求會同時讀到相同的計數而雙雙放行 ——
/// 這與 Stage 2 的 Read-Modify-Write 是完全相同的錯誤。
/// </summary>
public class RedisSlidingWindowRateLimiter : IDistributedRateLimiter
{
    /// <summary>
    /// KEYS[1] = 分區的 Sorted Set
    /// ARGV[1] = 現在時間（毫秒）
    /// ARGV[2] = 視窗長度（毫秒）
    /// ARGV[3] = 額度上限
    /// ARGV[4] = 這次請求的唯一成員名稱
    ///
    /// 回傳：{ 是否允許(1/0), 剩餘額度, 建議重試等待毫秒 }
    /// </summary>
    private const string Script = """
        local now = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local limit = tonumber(ARGV[3])
        local member = ARGV[4]

        -- 移除滑出視窗的紀錄
        redis.call('zremrangebyscore', KEYS[1], 0, now - window)

        local used = redis.call('zcard', KEYS[1])

        if used < limit then
            redis.call('zadd', KEYS[1], now, member)
            -- 每次都重設過期時間：閒置超過一個視窗後這個 Key 就沒有價值了，
            -- 不設的話 Redis 會累積無限多個再也用不到的分區。
            redis.call('pexpire', KEYS[1], window)
            return { 1, limit - used - 1, 0 }
        end

        -- 已滿。最舊的那一筆滑出視窗時就會空出額度。
        local oldest = redis.call('zrange', KEYS[1], 0, 0, 'WITHSCORES')
        local retry = window

        if oldest[2] then
            retry = math.ceil(tonumber(oldest[2]) + window - now)
            if retry < 1 then retry = 1 end
        end

        return { 0, 0, retry }
        """;

    private readonly IConnectionMultiplexer _connection;
    private readonly ILogger<RedisSlidingWindowRateLimiter> _logger;
    private readonly string _prefix;

    public RedisSlidingWindowRateLimiter(
        IConnectionMultiplexer connection,
        IOptions<RedisOptions> redisOptions,
        ILogger<RedisSlidingWindowRateLimiter> logger)
    {
        _connection = connection;
        _logger = logger;
        _prefix = redisOptions.Value.InstanceName + "rl:";
    }

    public async Task<RateLimitDecision> TryAcquireAsync(
        string partitionKey,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 時間戳由呼叫端提供。
            //
            // 前提：各 Instance 的時鐘必須大致同步（NTP）。
            // 偏差 X 毫秒會讓視窗邊界飄移 X 毫秒 —— 在秒級的視窗下
            // 幾十毫秒的偏差無關緊要，但若偏差達到視窗長度的量級就會失準。
            //
            // 另一個選擇是在 Lua 內呼叫 redis.call('TIME') 取得單一時間來源，
            // 代價是腳本變成非確定性指令，對某些 Redis 部署模式有額外限制。
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var result = await _connection.GetDatabase().ScriptEvaluateAsync(
                Script,
                new RedisKey[] { _prefix + partitionKey },
                new RedisValue[]
                {
                    now,
                    (long)window.TotalMilliseconds,
                    permitLimit,
                    // 同一毫秒內的多個請求必須是不同的成員，
                    // 否則 zadd 會覆寫而不是新增，計數就少算了。
                    $"{now}-{Guid.NewGuid():N}"
                });

            var values = (RedisValue[])result!;

            var allowed = (long)values[0] == 1;

            return allowed
                ? RateLimitDecision.Allow((long)values[1])
                : RateLimitDecision.Deny(
                    TimeSpan.FromMilliseconds((long)values[2]));
        }
        catch (Exception ex)
        {
            // Redis 掛掉時「放行」而不是「全部拒絕」。
            //
            // 這是刻意的取捨：限流是保護機制而不是業務規則，
            // 讓它的故障演變成全站不可用，比暫時失去保護更糟。
            _logger.LogError(
                ex, "Distributed rate limiter unavailable, allowing request.");

            return RateLimitDecision.Allow(0);
        }
    }
}
