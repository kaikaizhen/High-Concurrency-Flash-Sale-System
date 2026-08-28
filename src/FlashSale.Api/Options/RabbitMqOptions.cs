namespace FlashSale.Api.Options;

public class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = string.Empty;

    /// <summary>AMQP 埠。預設 5672（管理介面是另一個埠 15672，不要混用）。</summary>
    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// 關閉時，搶購一律走同步路徑（AtomicQueued 策略會被拒絕）。
    /// Stage 5 的 Before / After 量測靠切換這個旗標。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 訊息處理失敗時的最大重試次數，超過就送進 DLQ。
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// 重試佇列的停留時間（毫秒）。訊息在這裡等待 TTL 到期後，
    /// 由 Dead Letter 機制自動送回主佇列重新處理。
    /// </summary>
    public int RetryDelayMs { get; set; } = 5000;
}
