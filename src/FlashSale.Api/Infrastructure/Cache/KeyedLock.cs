using System.Collections.Concurrent;

namespace FlashSale.Api.Infrastructure.Cache;

public class KeyedLock : IKeyedLock
{
    /// <summary>
    /// 每個 Key 各自一把鎖，而不是整個 Dictionary 共用一把。
    ///
    /// 這裡的 Key 就是呼叫端傳進來的快取 Key（例如 <c>CacheKeys.Product(1)</c>
    /// 產生的 <c>"product:1"</c>）。不同 Key 之間完全不互相阻塞 ——
    /// 商品 1 快取 Miss 造成的排隊，不會連帶讓商品 2 的請求也跟著等待。
    /// 只有「同一個 Key 同時 Miss」的那群請求才會串行化，
    /// 這正是 Single Flight 要的效果：範圍精準到單一資源，不誤傷其他資源。
    /// </summary>
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public async Task<IDisposable> AcquireAsync(string key)
    {
        // AddOrUpdate 對同一個 key 是原子操作：
        //   - 字典裡還沒有這個 key   → 新建一把鎖（第一個到達的請求）
        //   - 字典裡已經有這個 key   → 沿用同一把鎖，只是引用計數 +1
        // 「同一個 key」在這裡的意思就是同一筆被快取的資源，
        // 例如同時湧入的 200 個請求都在查 product:1，就會全部沿用同一個 Entry。
        var entry = _entries.AddOrUpdate(
            key,
            _ => new Entry(),
            (_, existing) =>
            {
                existing.AddRef();
                return existing;
            });

        // 只有拿到「這個 key 專屬」的號誌，才會繼續往下走；
        // 排在後面的請求會卡在這裡，直到前面持有鎖的請求 Dispose 為止。
        await entry.Semaphore.WaitAsync();

        return new Releaser(this, key, entry);
    }

    /// <summary>
    /// 引用計數歸零時才把 Key 從字典移除，避免長時間執行後字典無限增長。
    /// </summary>
    private sealed class Entry
    {
        private int _refCount = 1;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public void AddRef()
        {
            Interlocked.Increment(ref _refCount);
        }

        public int Release()
        {
            return Interlocked.Decrement(ref _refCount);
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly KeyedLock _owner;

        /// <summary>
        /// 這把鎖對應的快取 Key。Dispose 時要清理的就是「_entries 字典裡
        /// 這一個 key 的項目」，而不是整個字典 —— 所以必須把 key 一路帶到這裡。
        /// </summary>
        private readonly string _key;

        private readonly Entry _entry;
        private bool _disposed;

        public Releaser(KeyedLock owner, string key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // 放行號誌，讓排在同一個 key 後面的下一個請求可以進來。
            _entry.Semaphore.Release();

            // 引用計數歸零，代表目前沒有任何請求在等這個 key 了，
            // 把它從字典移除 —— 否則每個處理過的商品 Id 都會在字典裡
            // 留下一筆 Entry，長時間執行後字典會無限增長。
            if (_entry.Release() == 0)
            {
                _owner._entries.TryRemove(
                    new KeyValuePair<string, Entry>(_key, _entry));
            }
        }
    }
}
