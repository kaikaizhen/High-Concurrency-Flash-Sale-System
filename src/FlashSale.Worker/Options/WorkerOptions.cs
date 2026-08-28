namespace FlashSale.Worker.Options;

public class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// 同時處理的訊息數（Prefetch Count）。
    ///
    /// 設 1 = 嚴格逐筆處理，最容易觀察佇列堆積。
    /// 調高可以提升吞吐，但也代表一次預取更多訊息 ——
    /// Worker 掛掉時這些訊息會回到佇列重新投遞，重複的風險變大。
    /// </summary>
    public ushort PrefetchCount { get; set; } = 1;

    /// <summary>
    /// 模擬每筆訂單的處理耗時（毫秒）。
    ///
    /// 計畫 §10 要求「每筆 Order 處理 100 ms」，用來製造出
    /// 「API 很快、後端很慢」的落差，好觀察佇列如何吸收尖峰。
    /// 正式環境設為 0。
    /// </summary>
    public int SimulatedProcessingMs { get; set; }
}
