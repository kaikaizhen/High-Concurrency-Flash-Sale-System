namespace FlashSale.Api.Infrastructure.Cache;

/// <summary>
/// 快取查詢結果。
///
/// 必須把「Key 不存在」和「Key 存在但值是 null」分開，
/// 否則無法實作負向快取 —— 兩者都回傳 null 的話，
/// 快取起來的「查無此商品」永遠會被當成 Miss 而重新查資料庫。
/// </summary>
public readonly struct CacheResult<T>
{
    private CacheResult(bool found, T? value)
    {
        Found = found;
        Value = value;
    }

    /// <summary>Key 是否存在於快取中（即使其值為 null）。</summary>
    public bool Found { get; }

    public T? Value { get; }

    public static CacheResult<T> Hit(T? value)
    {
        return new CacheResult<T>(true, value);
    }

    public static CacheResult<T> Miss()
    {
        return new CacheResult<T>(false, default);
    }
}

public interface ICacheService
{
    Task<CacheResult<T>> GetAsync<T>(string key);

    /// <summary>
    /// 寫入快取。<paramref name="value"/> 允許為 null（負向快取）。
    /// </summary>
    Task SetAsync<T>(string key, T? value, TimeSpan ttl);

    Task RemoveAsync(string key);
}
