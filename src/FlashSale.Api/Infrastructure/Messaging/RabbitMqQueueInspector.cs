using FlashSale.Api.Common.Constants;
using FlashSale.Api.Models.Dtos.Diagnostics;

namespace FlashSale.Api.Infrastructure.Messaging;

public class RabbitMqQueueInspector : IQueueInspector
{
    private readonly IRabbitMqConnectionProvider _connectionProvider;
    private readonly ILogger<RabbitMqQueueInspector> _logger;

    public RabbitMqQueueInspector(
        IRabbitMqConnectionProvider connectionProvider,
        ILogger<RabbitMqQueueInspector> logger)
    {
        _connectionProvider = connectionProvider;
        _logger = logger;
    }

    public async Task<QueueMetricsDtoModel> GetQueueMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await _connectionProvider
                .GetConnectionAsync(cancellationToken);

            await using var channel = await connection.CreateChannelAsync(
                cancellationToken: cancellationToken);

            // MessageCountAsync 走的是 passive declare，
            // 佇列不存在會丟例外而不是把它建出來。
            return new QueueMetricsDtoModel
            {
                Available = true,
                PendingOrders = await channel.MessageCountAsync(
                    MessagingConstants.OrderCreatedQueue, cancellationToken),
                PendingRetries = await channel.MessageCountAsync(
                    MessagingConstants.OrderCreatedRetryQueue, cancellationToken),
                DeadLettered = await channel.MessageCountAsync(
                    MessagingConstants.OrderCreatedDeadLetterQueue, cancellationToken)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read queue metrics.");

            return new QueueMetricsDtoModel { Available = false };
        }
    }
}
