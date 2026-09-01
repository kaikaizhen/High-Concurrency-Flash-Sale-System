namespace FlashSale.Api.Options;

public class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>服務名稱，會成為 Trace 與 Metric 上的 service.name。</summary>
    public string ServiceName { get; set; } = "flashsale-api";

    /// <summary>
    /// OTLP 端點。空字串代表不匯出 —— 適用於單純跑測試、
    /// 不想依賴外部收集器的情況。
    /// </summary>
    public string OtlpEndpoint { get; set; } = string.Empty;

    public bool TracingEnabled { get; set; } = true;

    public bool MetricsEnabled { get; set; } = true;

    /// <summary>
    /// Trace 取樣率（0.0 ~ 1.0）。
    ///
    /// 壓測時務必調低。每個請求都產生完整 Trace 的話，
    /// **觀測本身會成為瓶頸** —— 那會讓量到的數字失真，
    /// 而觀測系統的第一守則就是不能改變被觀測的對象。
    /// </summary>
    public double TraceSampleRatio { get; set; } = 1.0;
}
