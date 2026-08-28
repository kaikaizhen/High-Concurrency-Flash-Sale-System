using System.Collections.Concurrent;

namespace FlashSale.Api.Infrastructure.Cache;

public class KeyedLock : IKeyedLock
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public async Task<IDisposable> AcquireAsync(string key)
    {
        var entry = _entries.AddOrUpdate(
            key,
            _ => new Entry(),
            (_, existing) =>
            {
                existing.AddRef();
                return existing;
            });

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

            _entry.Semaphore.Release();

            if (_entry.Release() == 0)
            {
                _owner._entries.TryRemove(
                    new KeyValuePair<string, Entry>(_key, _entry));
            }
        }
    }
}
