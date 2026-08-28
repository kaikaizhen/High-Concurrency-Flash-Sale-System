using FlashSale.Api.Models.Dtos.Diagnostics;

namespace FlashSale.Api.Infrastructure.Messaging;

/// <summary>
/// 讀取佇列的待處理訊息數。
///
/// 用 AMQP 的 passive queue declare 取得，不依賴 RabbitMQ 管理外掛
/// （管理 API 在 15672，不一定對外開放）。
/// </summary>
public interface IQueueInspector
{
    Task<QueueMetricsDtoModel> GetQueueMetricsAsync(
        CancellationToken cancellationToken = default);
}
