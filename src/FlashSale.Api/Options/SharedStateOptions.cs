namespace FlashSale.Api.Options;

/// <summary>
/// 跨 Instance 共用狀態的開關。
///
/// Stage 1–7 有三處狀態存在行程記憶體中，單一 Instance 時完全正確，
/// 多 Instance 時全部失準：
///
///   MetricsCollector  每台只看得到自己的數字
///   KeyedLock         N 台 = N 個獨立的鎖，Single Flight 保護降為 1/N
///   RateLimiter       N 台 = N 份額度，實際限制變成 N 倍
///
/// 這裡讓三者都能在「行程內」與「Redis 共用」之間切換，
/// 以便在同一套環境下量測差異 —— 沿用 Stage 3 / 6 的對照組做法。
///
/// 正式環境三個都應該是 true。
/// </summary>
public class SharedStateOptions
{
    public const string SectionName = "SharedState";

    /// <summary>計數器改用 Redis，讓任一台都能讀到全貌。</summary>
    public bool DistributedMetrics { get; set; } = true;

    /// <summary>Single Flight 改用 Redis 分散式鎖。</summary>
    public bool DistributedLock { get; set; } = true;

    /// <summary>限流改用 Redis 共用額度（取代 ASP.NET 內建的行程內限流器）。</summary>
    public bool DistributedRateLimit { get; set; } = true;
}
