using FlashSale.Api.Common.Enums;

namespace FlashSale.Api.Options;

public class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>
    /// 總開關。
    ///
    /// 注意：開啟後 Stage 2–6 的壓測腳本（單一來源送出數千個請求）
    /// 會被全域 per-IP 限制擋下。要重現那些階段的結果，
    /// 必須先 <c>$env:RateLimit__Enabled="false"</c>。
    /// 這不是缺陷 —— 加了限流之後壓測本來就得考慮它的存在。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>全域 per-IP 限制：保護整個 API 不被單一來源灌爆。</summary>
    public IpLimitOptions PerIp { get; set; } = new();

    /// <summary>搶購端點的 per-User 限制：防止單一使用者洗版。</summary>
    public FlashSaleLimitOptions FlashSale { get; set; } = new();

    public class IpLimitOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>每個視窗允許的請求數。</summary>
        public int PermitLimit { get; set; } = 600;

        public int WindowSeconds { get; set; } = 60;

        /// <summary>
        /// 排隊等待的請求數上限。
        ///
        /// 設 0 = 超過就立刻拒絕。
        /// 大於 0 會讓超額請求排隊等下一個視窗，但那會佔住連線 ——
        /// 秒殺場景下「快速拒絕」比「讓他等」對系統更友善。
        /// </summary>
        public int QueueLimit { get; set; }
    }

    public class FlashSaleLimitOptions
    {
        public bool Enabled { get; set; } = true;

        public RateLimitAlgorithm Algorithm { get; set; } =
            RateLimitAlgorithm.SlidingWindow;

        public int PermitLimit { get; set; } = 10;

        public int WindowSeconds { get; set; } = 1;

        public int QueueLimit { get; set; }

        /// <summary>SlidingWindow 專用：把視窗切成幾段。段數越多越平滑，記憶體越多。</summary>
        public int SegmentsPerWindow { get; set; } = 4;

        /// <summary>TokenBucket 專用：桶子容量，也就是允許的最大突發量。</summary>
        public int TokenLimit { get; set; } = 10;

        /// <summary>TokenBucket 專用：每次補充週期補幾個權杖。</summary>
        public int TokensPerPeriod { get; set; } = 10;

        /// <summary>TokenBucket 專用：補充週期（秒）。</summary>
        public int ReplenishmentPeriodSeconds { get; set; } = 1;

        /// <summary>Concurrency 專用：同時進行中的請求數上限。</summary>
        public int ConcurrencyLimit { get; set; } = 10;
    }
}
