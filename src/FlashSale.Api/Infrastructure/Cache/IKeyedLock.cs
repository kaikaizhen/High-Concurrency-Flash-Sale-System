namespace FlashSale.Api.Infrastructure.Cache;

/// <summary>
/// 以 Key 為單位的互斥鎖，用於 Single Flight：
/// 同一個 Key 同時 Miss 時，只讓第一個請求去查資料庫，其餘等它的結果。
///
/// 已知限制：鎖只存在於單一 Instance 的記憶體中。
/// Stage 8 導入多 Instance 之後，N 台機器就會有 N 個各自獨立的鎖，
/// 保護效果降為 1/N —— 屆時需要 Redis 分散式鎖。
/// </summary>
public interface IKeyedLock
{
    /// <param name="key">
    /// 要保護的資源識別碼，這裡固定就是快取 Key
    /// （例如 <c>CacheKeys.Product(productId)</c> 產生的 <c>"product:1"</c>）。
    /// 只有傳入相同 key 的呼叫才會互相排隊；不同 key 完全獨立、互不阻塞。
    /// </param>
    /// <returns>
    /// 代表「持有這把鎖」的 handle，Dispose（搭配 <c>using</c>）時釋放，
    /// 讓下一個排隊等同一個 key 的請求可以進入。
    /// </returns>
    Task<IDisposable> AcquireAsync(string key);
}
