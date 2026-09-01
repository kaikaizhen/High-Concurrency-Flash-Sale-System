using FlashSale.Api.Common.Enums;

namespace FlashSale.Api.Infrastructure.Idempotency;

/// <summary>
/// 一筆冪等記錄。
/// </summary>
public class IdempotencyEntry
{
    public IdempotencyStatus Status { get; set; }

    /// <summary>完成時保存的 HTTP 狀態碼，用於回放。</summary>
    public int StatusCode { get; set; }

    /// <summary>完成時保存的回應內容（JSON）。</summary>
    public string? ResponseBody { get; set; }
}

public interface IIdempotencyStore
{
    /// <summary>
    /// 嘗試佔用一個 Key。
    ///
    /// **這個操作必須是原子的** —— 「檢查是否存在」與「建立記錄」若分成兩步，
    /// 兩個併發請求會同時通過檢查，冪等保證就失效了。
    /// Redis 用 SET NX，SQL Server 用主鍵衝突，都是把判斷交給儲存層。
    /// </summary>
    /// <returns>
    /// null = 佔用成功，呼叫端是第一個，應該繼續執行。
    /// 非 null = 已被佔用，內容為既有記錄。
    /// </returns>
    Task<IdempotencyEntry?> TryAcquireAsync(string key, TimeSpan ttl);

    /// <summary>
    /// 標記為完成並保存回應，供後續重送回放。
    /// </summary>
    Task CompleteAsync(string key, int statusCode, string? responseBody, TimeSpan ttl);

    /// <summary>
    /// 釋放佔用。
    ///
    /// 請求失敗時必須呼叫，否則這個 Key 會卡在 InProgress 直到 TTL 到期，
    /// 使用者重試會一直收到「處理中」而無法真正重試。
    /// </summary>
    Task ReleaseAsync(string key);
}
