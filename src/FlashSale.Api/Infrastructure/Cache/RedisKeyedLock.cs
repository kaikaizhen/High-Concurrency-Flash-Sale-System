using FlashSale.Api.Options;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FlashSale.Api.Infrastructure.Cache;

/// <summary>
/// 跨 Instance 的 Key 層級互斥鎖。
///
/// <see cref="KeyedLock"/> 的鎖只存在於單一行程的記憶體中：
/// N 台機器就有 N 個各自獨立的鎖，Single Flight 的保護效果降為 1/N。
/// Stage 4 的註解已經標記這個問題，這裡是它的解法。
///
/// 實作要點：
///
/// 1. <c>SET key token NX PX ttl</c> —— 取得鎖。NX 保證只有一個人成功。
/// 2. TTL 是必要的：持有者當機時鎖必須自己過期，否則整個 Key 永久卡死。
/// 3. 釋放時用 Lua 比對 token —— 只能釋放**自己**的鎖。
///    直接 DEL 的話，若自己的鎖已因逾時被別人取得，就會誤刪別人的鎖。
/// </summary>
public class RedisKeyedLock : IKeyedLock
{
    /// <summary>
    /// 只有 token 相符才刪除。
    ///
    /// 這段必須是 Lua（在 Redis 內原子執行）——
    /// 拆成「GET 比對 → DEL」兩步的話，兩者之間鎖可能剛好過期並被別人取得，
    /// 於是刪掉了別人的鎖。
    /// </summary>
    private const string ReleaseScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        else
            return 0
        end
        """;

    /// <summary>
    /// 鎖的存活時間。必須大於「查資料庫 + 寫回快取」的正常耗時，
    /// 否則工作還沒做完鎖就過期，Single Flight 形同虛設。
    /// </summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 等待鎖的上限。等不到就直接放行去查資料庫 ——
    /// 讓使用者多一次資料庫查詢，好過讓他一直等。
    /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private readonly IConnectionMultiplexer _connection;
    private readonly ILogger<RedisKeyedLock> _logger;
    private readonly string _prefix;

    public RedisKeyedLock(
        IConnectionMultiplexer connection,
        IOptions<RedisOptions> redisOptions,
        ILogger<RedisKeyedLock> logger)
    {
        _connection = connection;
        _logger = logger;
        _prefix = redisOptions.Value.InstanceName + "lock:";
    }

    public async Task<IDisposable> AcquireAsync(string key)
    {
        var redisKey = _prefix + key;

        // 每次取得鎖都用一個新的 token，用來證明「這把鎖是我的」。
        var token = Guid.NewGuid().ToString("N");

        var db = _connection.GetDatabase();
        var deadline = DateTime.UtcNow.Add(MaxWait);

        while (true)
        {
            try
            {
                var acquired = await db.StringSetAsync(
                    redisKey, token, LockTtl, When.NotExists);

                if (acquired)
                {
                    return new RedisLockHandle(this, redisKey, token);
                }
            }
            catch (Exception ex)
            {
                // Redis 掛掉時退化成「不上鎖」而不是讓請求失敗。
                // 少了 Single Flight 只是多幾次資料庫查詢，
                // 讓請求整個失敗才是災難。
                _logger.LogError(ex, "Distributed lock unavailable. Key={Key}", key);

                return NoOpHandle.Instance;
            }

            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogWarning(
                    "Distributed lock wait timed out, proceeding without it. Key={Key}",
                    key);

                return NoOpHandle.Instance;
            }

            await Task.Delay(PollInterval);
        }
    }

    private async Task ReleaseAsync(string redisKey, string token)
    {
        try
        {
            await _connection.GetDatabase().ScriptEvaluateAsync(
                ReleaseScript,
                new RedisKey[] { redisKey },
                new RedisValue[] { token });
        }
        catch (Exception ex)
        {
            // 釋放失敗不致命 —— TTL 會讓鎖自己過期。
            _logger.LogWarning(ex, "Failed to release lock. Key={Key}", redisKey);
        }
    }

    private sealed class RedisLockHandle : IDisposable
    {
        private readonly RedisKeyedLock _owner;
        private readonly string _redisKey;
        private readonly string _token;
        private bool _disposed;

        public RedisLockHandle(RedisKeyedLock owner, string redisKey, string token)
        {
            _owner = owner;
            _redisKey = redisKey;
            _token = token;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // IDisposable 沒有非同步版本，這裡不能 await。
            // 用 fire-and-forget 並吞掉例外 —— 釋放失敗最壞就是等 TTL。
            _ = _owner.ReleaseAsync(_redisKey, _token);
        }
    }

    /// <summary>取不到鎖時的替身：不做任何事，讓呼叫端照常往下走。</summary>
    private sealed class NoOpHandle : IDisposable
    {
        public static readonly NoOpHandle Instance = new();

        public void Dispose()
        {
        }
    }
}
