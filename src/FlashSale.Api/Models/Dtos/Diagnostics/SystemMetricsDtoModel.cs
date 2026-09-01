namespace FlashSale.Api.Models.Dtos.Diagnostics;

/// <summary>
/// 計畫 §14 要求的系統面指標。
///
/// 這些數字的用途是**定位瓶頸**：光看 RPS 與 P99 只知道「慢」，
/// 不知道慢在哪裡。CPU 滿載、連線池耗盡、Redis 變慢、佇列堆積，
/// 每一種的解法完全不同。
/// </summary>
public class SystemMetricsDtoModel
{
    public string InstanceId { get; set; } = string.Empty;

    public ProcessMetrics Process { get; set; } = new();

    public DependencyMetrics Database { get; set; } = new();

    public DependencyMetrics Redis { get; set; } = new();

    public QueueMetricsDtoModel Queue { get; set; } = new();

    public class ProcessMetrics
    {
        /// <summary>
        /// 自上次查詢以來的平均 CPU 使用率（%），已除以邏輯處理器數。
        ///
        /// 第一次查詢沒有基準點，會回傳 0 —— 壓測時應先呼叫一次暖身。
        /// </summary>
        public double CpuPercent { get; set; }

        /// <summary>行程實際佔用的實體記憶體（MB）。</summary>
        public double WorkingSetMb { get; set; }

        /// <summary>Managed 堆的大小（MB）。與 WorkingSet 的差距就是非受控記憶體。</summary>
        public double GcHeapMb { get; set; }

        /// <summary>
        /// 執行緒數。
        ///
        /// 突然暴增通常代表有同步阻塞把執行緒池吃光了 ——
        /// ThreadPool 會以每秒補一兩條的速度慢慢長，那段期間延遲會非常難看。
        /// </summary>
        public int ThreadCount { get; set; }

        public int ProcessorCount { get; set; }
    }

    public class DependencyMetrics
    {
        /// <summary>一次最輕量往返的耗時（毫秒）。</summary>
        public double LatencyMs { get; set; }

        /// <summary>
        /// 目前對該相依的連線數。Redis 不適用時為 -1。
        ///
        /// 接近連線池上限（SqlClient 預設 100）就是瓶頸警訊 ——
        /// 後續請求會排隊等連線，表現為延遲暴增但 CPU 很閒。
        /// </summary>
        public int Connections { get; set; } = -1;

        public bool Available { get; set; }
    }
}
