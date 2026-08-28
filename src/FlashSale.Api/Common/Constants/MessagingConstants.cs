namespace FlashSale.Api.Common.Constants;

/// <summary>
/// RabbitMQ 拓撲。三個 Exchange 各司其職：
///
///     OrderExchange  ──order.created──▶  OrderCreatedQueue      正常流程
///                          ▲
///                          │ TTL 到期後由 Dead Letter 自動送回
///                          │
///     RetryExchange  ──order.created──▶  OrderCreatedRetryQueue 暫留重試
///
///     DeadLetterExchange ──order.created──▶ OrderCreatedDeadLetterQueue  人工處理
///
/// 重試不是靠「NACK + requeue」。那會讓失敗訊息立刻回到佇列頭部被重新取出，
/// 形成沒有間隔的忙碌迴圈，把 Consumer 和下游一起拖垮。
/// 改成把訊息重新發布到帶 TTL 的重試佇列，等待期間 Consumer 可以處理其他訊息。
/// </summary>
public static class MessagingConstants
{
    public const string OrderExchange = "flashsale.orders";
    public const string RetryExchange = "flashsale.orders.retry";
    public const string DeadLetterExchange = "flashsale.orders.dlx";

    public const string OrderCreatedRoutingKey = "order.created";

    public const string OrderCreatedQueue = "flashsale.orders.created";
    public const string OrderCreatedRetryQueue = "flashsale.orders.created.retry";
    public const string OrderCreatedDeadLetterQueue = "flashsale.orders.created.dlq";

    /// <summary>
    /// 記錄該訊息已經重試過幾次。放在 Header 而不是訊息本體，
    /// 因為這是傳輸層的關注點，不屬於商業資料。
    /// </summary>
    public const string RetryCountHeader = "x-flashsale-retry-count";

    /// <summary>失敗原因，僅供 DLQ 人工排查使用。</summary>
    public const string FailureReasonHeader = "x-flashsale-failure-reason";
}
