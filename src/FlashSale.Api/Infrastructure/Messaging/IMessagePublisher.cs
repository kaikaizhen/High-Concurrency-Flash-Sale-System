namespace FlashSale.Api.Infrastructure.Messaging;

public interface IMessagePublisher
{
    /// <summary>
    /// 序列化為 JSON 後發布，並等待 Broker 確認（Publisher Confirms）。
    ///
    /// 不等確認就回應客戶端，等於在說「訂單收到了」卻不確定訊息有沒有進佇列。
    /// 庫存已經扣掉、訊息卻遺失的話，那件商品就永遠賣不出去也沒有訂單。
    /// </summary>
    Task PublishAsync<T>(
        string exchange,
        string routingKey,
        T message,
        IDictionary<string, object?>? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 原封不動地發布既有的訊息本體。
    ///
    /// 用於重試與 DLQ 轉送：那裡要搬運的是「收到的原始位元組」，
    /// 不能反序列化後再序列化 —— 因為無法解析的毒訊息也必須能送進 DLQ，
    /// 而且轉送過程不應該改變訊息內容。
    /// </summary>
    Task PublishRawAsync(
        string exchange,
        string routingKey,
        ReadOnlyMemory<byte> body,
        IDictionary<string, object?>? headers = null,
        CancellationToken cancellationToken = default);
}
