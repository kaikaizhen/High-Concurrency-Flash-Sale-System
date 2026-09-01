namespace FlashSale.Api.Options;

public enum IdempotencyProvider
{
    Redis = 0,
    SqlServer = 1
}

public class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>
    /// 關閉時完全不檢查 Idempotency-Key。
    /// Stage 6 的 Before / After 量測靠切換這個旗標。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 計畫 §11 要求比較兩種儲存方式，因此做成可切換。
    /// </summary>
    public IdempotencyProvider Provider { get; set; } = IdempotencyProvider.Redis;

    /// <summary>
    /// 記錄保留時間（秒）。
    ///
    /// 這個值就是「重送保護的有效期限」：超過之後同一個 Key 會被當成新請求。
    /// 太短則客戶端稍晚的重試會建立第二筆訂單；
    /// 太長則 SQL Server 版的記錄表會無限增長（Redis 版會自動過期）。
    /// </summary>
    public int TtlSeconds { get; set; } = 86400;

    /// <summary>
    /// 是否強制要求 Idempotency-Key。
    ///
    /// 預設 false —— 沒帶 Key 的請求照常處理，只是不受保護。
    /// 若整個系統的客戶端都已經支援，可以設為 true 讓漏帶的請求直接被拒絕。
    /// </summary>
    public bool Required { get; set; }
}
