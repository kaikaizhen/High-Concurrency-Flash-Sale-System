using FlashSale.Api.Common.Constants;
using FlashSale.Api.Options;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FlashSale.Api.Infrastructure.Messaging;

/// <summary>
/// 宣告 Exchange、Queue 與其綁定關係。
///
/// AMQP 的宣告是**冪等**的：同樣的參數重複宣告不會出錯，
/// 所以 API 與 Worker 兩邊都可以在啟動時各自宣告一次，
/// 誰先啟動都不影響。
///
/// 但參數若不一致會直接失敗（PRECONDITION_FAILED），
/// 這也是為什麼拓撲定義要集中在這一個檔案，兩邊共用同一份程式碼。
/// </summary>
public class RabbitMqTopology
{
    private readonly IRabbitMqConnectionProvider _connectionProvider;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqTopology> _logger;

    public RabbitMqTopology(
        IRabbitMqConnectionProvider connectionProvider,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqTopology> logger)
    {
        _connectionProvider = connectionProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task DeclareAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);

        await using var channel = await connection.CreateChannelAsync(
            cancellationToken: cancellationToken);

        // ---- Exchange ----
        // Direct：依 Routing Key 精確比對。
        // 目前只有一種事件，用 Topic 也可以，但 Direct 更能表達
        // 「這裡沒有萬用字元路由」的意圖。
        foreach (var exchange in new[]
                 {
                     MessagingConstants.OrderExchange,
                     MessagingConstants.RetryExchange,
                     MessagingConstants.DeadLetterExchange
                 })
        {
            await channel.ExchangeDeclareAsync(
                exchange: exchange,
                type: ExchangeType.Direct,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken);
        }

        // ---- 主佇列 ----
        // durable + 非 autoDelete：Broker 重啟後佇列與訊息都還在。
        // 沒有這個，秒殺期間 Broker 一重啟，未處理的訂單就全部消失。
        await channel.QueueDeclareAsync(
            queue: MessagingConstants.OrderCreatedQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: MessagingConstants.OrderCreatedQueue,
            exchange: MessagingConstants.OrderExchange,
            routingKey: MessagingConstants.OrderCreatedRoutingKey,
            cancellationToken: cancellationToken);

        // ---- 重試佇列 ----
        // 沒有 Consumer。訊息在這裡待滿 TTL 後，由 Dead Letter 機制
        // 自動送回主 Exchange，形成「延遲重試」。
        await channel.QueueDeclareAsync(
            queue: MessagingConstants.OrderCreatedRetryQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = _options.RetryDelayMs,
                ["x-dead-letter-exchange"] = MessagingConstants.OrderExchange,
                ["x-dead-letter-routing-key"] = MessagingConstants.OrderCreatedRoutingKey
            },
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: MessagingConstants.OrderCreatedRetryQueue,
            exchange: MessagingConstants.RetryExchange,
            routingKey: MessagingConstants.OrderCreatedRoutingKey,
            cancellationToken: cancellationToken);

        // ---- Dead Letter Queue ----
        // 終點站，沒有 TTL 也沒有自動轉送。訊息留在這裡等待人工排查。
        // 不設 TTL 是刻意的：自動丟棄失敗訂單等於靜默地失去客戶的錢。
        await channel.QueueDeclareAsync(
            queue: MessagingConstants.OrderCreatedDeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: MessagingConstants.OrderCreatedDeadLetterQueue,
            exchange: MessagingConstants.DeadLetterExchange,
            routingKey: MessagingConstants.OrderCreatedRoutingKey,
            cancellationToken: cancellationToken);

        _logger.LogInformation("RabbitMQ topology declared.");
    }
}
