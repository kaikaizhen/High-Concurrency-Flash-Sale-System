using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace FlashSale.Api.Infrastructure.Messaging;

public class RabbitMqMessagePublisher : IMessagePublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IChannelPool _channelPool;
    private readonly ILogger<RabbitMqMessagePublisher> _logger;

    public RabbitMqMessagePublisher(
        IChannelPool channelPool,
        ILogger<RabbitMqMessagePublisher> logger)
    {
        _channelPool = channelPool;
        _logger = logger;
    }

    public Task PublishAsync<T>(
        string exchange,
        string routingKey,
        T message,
        IDictionary<string, object?>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var body = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(message, SerializerOptions));

        return PublishRawAsync(exchange, routingKey, body, headers, cancellationToken);
    }

    public async Task PublishRawAsync(
        string exchange,
        string routingKey,
        ReadOnlyMemory<byte> body,
        IDictionary<string, object?>? headers = null,
        CancellationToken cancellationToken = default)
    {
        // IChannel 不是執行緒安全的，所以要獨佔使用；
        // 但開啟 Channel 需要一次 AMQP 往返，每次發布都新建會很貴，
        // 因此從池子租借、用完歸還。
        await using var pooled = await _channelPool.RentAsync(cancellationToken);

        var properties = new BasicProperties
        {
            // Persistent：訊息會寫入磁碟，Broker 重啟後仍在。
            // 搭配 durable queue 才有意義 —— 只做其中一個都會遺失訊息。
            Persistent = true,
            ContentType = "application/json"
        };

        if (headers is not null)
        {
            properties.Headers = headers;
        }

        // BasicPublishAsync 在啟用 Publisher Confirms 時會等待 Broker 的 ack。
        await pooled.Channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);

        _logger.LogDebug(
            "Message published. Exchange={Exchange} RoutingKey={RoutingKey}",
            exchange,
            routingKey);
    }
}
