using System.Text;
using System.Text.Json;
using FlashSale.Api.Common.Constants;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Infrastructure.Messaging;
using FlashSale.Api.Infrastructure.Observability;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Models.Messages;
using FlashSale.Api.Options;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Worker.Options;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FlashSale.Worker;

/// <summary>
/// 消費 OrderCreated 事件並建立訂單。
///
/// 這是「填谷」的那一端：API 以任意速度把訊息丟進佇列，
/// 這裡以自己能負荷的速度慢慢消化。兩邊的速度**不需要相同**。
/// </summary>
public class OrderCreatedConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IRabbitMqConnectionProvider _connectionProvider;
    private readonly RabbitMqTopology _topology;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessagePublisher _publisher;
    private readonly RabbitMqOptions _rabbitMqOptions;
    private readonly WorkerOptions _workerOptions;
    private readonly ILogger<OrderCreatedConsumer> _logger;

    public OrderCreatedConsumer(
        IRabbitMqConnectionProvider connectionProvider,
        RabbitMqTopology topology,
        IServiceScopeFactory scopeFactory,
        IMessagePublisher publisher,
        IOptions<RabbitMqOptions> rabbitMqOptions,
        IOptions<WorkerOptions> workerOptions,
        ILogger<OrderCreatedConsumer> logger)
    {
        _connectionProvider = connectionProvider;
        _topology = topology;
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _rabbitMqOptions = rabbitMqOptions.Value;
        _workerOptions = workerOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _topology.DeclareAsync(stoppingToken);

        var connection = await _connectionProvider.GetConnectionAsync(stoppingToken);

        var channel = await connection.CreateChannelAsync(
            cancellationToken: stoppingToken);

        // Prefetch：一次最多預取幾則未 ACK 的訊息。
        // 不設的話 Broker 會把整個佇列一次推給這個 Consumer，
        // 多開幾個 Worker 也不會分擔到工作 —— 訊息早就全被第一個拿走了。
        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: _workerOptions.PrefetchCount,
            global: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (_, ea) =>
            await HandleAsync(channel, ea, stoppingToken);

        // autoAck: false —— 必須自己 ACK。
        // 設 true 的話訊息一送出就被視為處理完成，
        // Worker 在建立訂單前掛掉，那筆訂單就永遠消失了。
        await channel.BasicConsumeAsync(
            queue: MessagingConstants.OrderCreatedQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Order consumer started. Prefetch={Prefetch} SimulatedProcessingMs={Delay}",
            _workerOptions.PrefetchCount,
            _workerOptions.SimulatedProcessingMs);

        // BackgroundService 的 ExecuteAsync 一旦返回，整個服務就會停止。
        // Consumer 是由 RabbitMQ 客戶端的執行緒驅動的，所以這裡必須掛著等待。
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 正常關閉
        }
        finally
        {
            await channel.DisposeAsync();
        }
    }

    private async Task HandleAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        CancellationToken cancellationToken)
    {
        // Stage 10：Consumer 端的 Span。
        //
        // 它與 API 端的 Producer Span 是同一條業務鏈路的兩端，
        // 但**不是同一個 Trace** —— 訊息裡沒有傳遞 traceparent。
        // 要讓兩端串起來需要在發布時把 W3C Trace Context 寫進 Header、
        // 消費時取出並設為 Parent，本階段未實作（見 docs/final-analysis.md）。
        using var activity = FlashSaleActivitySource.StartConsume(
            MessagingConstants.OrderCreatedQueue);

        OrderCreatedMessage? message;

        // ---- 第一種失敗：訊息本身無法解析 ----
        // 這種訊息重試一百次也不會突然變得可以解析，
        // 直接送 DLQ，不要浪費重試額度也不要卡住佇列。
        try
        {
            message = JsonSerializer.Deserialize<OrderCreatedMessage>(
                Encoding.UTF8.GetString(ea.Body.Span),
                SerializerOptions);

            if (message is null)
            {
                throw new JsonException("Message body deserialized to null.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Poison message, sending straight to DLQ. DeliveryTag={Tag}",
                ea.DeliveryTag);

            await SendToDeadLetterAsync(ea, $"Malformed message: {ex.Message}");
            await channel.BasicAckAsync(ea.DeliveryTag, false, cancellationToken);

            return;
        }

        // ---- 正常處理 ----
        try
        {
            await CreateOrderAsync(message, cancellationToken);

            // 只有真的寫進資料庫了才 ACK。
            await channel.BasicAckAsync(ea.DeliveryTag, false, cancellationToken);

            _logger.LogDebug(
                "Order created. MessageId={MessageId} ProductId={ProductId}",
                message.MessageId,
                message.ProductId);
        }
        catch (Exception ex)
        {
            // ---- 第二種失敗：暫時性錯誤（資料庫斷線、逾時…） ----
            // 這種有機會在稍後成功，值得重試。
            await HandleFailureAsync(channel, ea, ex, cancellationToken);
        }
    }

    private async Task CreateOrderAsync(
        OrderCreatedMessage message,
        CancellationToken cancellationToken)
    {
        if (_workerOptions.SimulatedProcessingMs > 0)
        {
            await Task.Delay(_workerOptions.SimulatedProcessingMs, cancellationToken);
        }

        // BackgroundService 是 Singleton，Repository 是 Scoped，
        // 因此每則訊息都要自己開一個 Scope，
        // 否則整個 Worker 生命週期共用一個 DbContext，變更追蹤會無限累積。
        using var scope = _scopeFactory.CreateScope();

        var orderRepository = scope.ServiceProvider
            .GetRequiredService<IOrderRepository>();

        // Stage 6：以 MessageId 去重。
        //
        // RabbitMQ 保證的是 at-least-once —— 重試、Worker 在 ACK 前崩潰、
        // 網路重送，都會讓同一則訊息被消費兩次。
        //
        // 去重不是「先查有沒有再新增」（併發下兩個都會通過查詢），
        // 而是把 MessageId 寫進 Order.IdempotencyKey，
        // 由資料庫的篩選唯一索引擋掉第二筆。
        var created = await orderRepository.TryCreateAsync(new Order
        {
            UserId = message.UserId,
            ProductId = message.ProductId,
            Quantity = message.Quantity,
            Status = OrderStatus.Completed,
            CreatedAt = message.OccurredAt,

            // 用訊息帶來的 Key（客戶端的 Idempotency-Key，或退回 MessageId），
            // 而不是 MessageId 本身 —— 見 OrderCreatedMessage.IdempotencyKey 的說明。
            IdempotencyKey = string.IsNullOrWhiteSpace(message.IdempotencyKey)
                ? message.MessageId.ToString()
                : message.IdempotencyKey
        });

        if (!created)
        {
            // 重複投遞。訂單早就建好了，這不是錯誤 ——
            // 直接視為成功並 ACK，否則會無限重試一則永遠「失敗」的訊息。
            _logger.LogInformation(
                "Duplicate message ignored, order already exists. MessageId={MessageId}",
                message.MessageId);
        }
    }

    private async Task HandleFailureAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var retryCount = GetRetryCount(ea);

        if (retryCount < _rabbitMqOptions.MaxRetryCount)
        {
            _logger.LogWarning(
                exception,
                "Processing failed, scheduling retry {Next}/{Max}.",
                retryCount + 1,
                _rabbitMqOptions.MaxRetryCount);

            // 重新發布到重試佇列。訊息會在那裡待滿 TTL，
            // 再由 Dead Letter 機制自動送回主佇列。
            //
            // 這裡刻意不用 BasicNack(requeue: true)：那會讓訊息立刻回到
            // 佇列頭部被馬上重新取出，形成沒有間隔的忙碌迴圈 ——
            // 資料庫正在掛掉的時候，這只會讓它掛得更徹底。
            await RepublishAsync(
                ea,
                MessagingConstants.RetryExchange,
                new Dictionary<string, object?>
                {
                    [MessagingConstants.RetryCountHeader] = retryCount + 1
                });
        }
        else
        {
            _logger.LogError(
                exception,
                "Retry limit reached, sending to DLQ. Retries={Retries}",
                retryCount);

            await SendToDeadLetterAsync(ea, exception.Message);
        }

        // 無論轉去重試或 DLQ，原本這一則都已經被安置好了，可以 ACK。
        // 不 ACK 的話它會一直佔著 unacked 額度，Prefetch 很快就被用光。
        await channel.BasicAckAsync(ea.DeliveryTag, false, cancellationToken);
    }

    private Task SendToDeadLetterAsync(
        BasicDeliverEventArgs ea,
        string reason)
    {
        return RepublishAsync(
            ea,
            MessagingConstants.DeadLetterExchange,
            new Dictionary<string, object?>
            {
                [MessagingConstants.RetryCountHeader] = GetRetryCount(ea),
                [MessagingConstants.FailureReasonHeader] =
                    Encoding.UTF8.GetBytes(Truncate(reason, 500))
            });
    }

    /// <summary>
    /// 原封不動地把訊息本體轉發到另一個 Exchange，只換掉 Header。
    /// 用 raw bytes 而非反序列化後再序列化 —— 無法解析的毒訊息也要能進 DLQ。
    /// </summary>
    private async Task RepublishAsync(
        BasicDeliverEventArgs ea,
        string exchange,
        IDictionary<string, object?> headers)
    {
        await _publisher.PublishRawAsync(
            exchange,
            MessagingConstants.OrderCreatedRoutingKey,
            ea.Body.ToArray(),
            headers);
    }

    private static int GetRetryCount(BasicDeliverEventArgs ea)
    {
        if (ea.BasicProperties.Headers is null ||
            !ea.BasicProperties.Headers.TryGetValue(
                MessagingConstants.RetryCountHeader, out var raw) ||
            raw is null)
        {
            return 0;
        }

        return raw switch
        {
            int value => value,
            long value => (int)value,
            byte[] bytes => int.TryParse(
                Encoding.UTF8.GetString(bytes), out var parsed) ? parsed : 0,
            _ => 0
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }
}
