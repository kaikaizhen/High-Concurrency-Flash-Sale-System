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
    Task<IDisposable> AcquireAsync(string key);
}
