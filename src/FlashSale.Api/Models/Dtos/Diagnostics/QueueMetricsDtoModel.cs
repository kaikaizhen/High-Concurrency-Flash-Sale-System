namespace FlashSale.Api.Models.Dtos.Diagnostics;

public class QueueMetricsDtoModel
{
    /// <summary>主佇列待處理訊息數 —— 這就是「削峰填谷」的那個峰。</summary>
    public uint PendingOrders { get; set; }

    /// <summary>等待 TTL 到期後重新投遞的訊息數。</summary>
    public uint PendingRetries { get; set; }

    /// <summary>重試用盡、需要人工處理的訊息數。不應該持續增長。</summary>
    public uint DeadLettered { get; set; }

    /// <summary>Broker 是否可連線。false 時上面三個數字沒有意義。</summary>
    public bool Available { get; set; }
}
