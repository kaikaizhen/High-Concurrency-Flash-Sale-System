using RabbitMQ.Client;

namespace FlashSale.Api.Infrastructure.Messaging;

/// <summary>
/// 共用單一 TCP 連線。
///
/// RabbitMQ 的 Connection 建立成本高（TCP + TLS + AMQP handshake），
/// 每個請求各開一條會直接把 Broker 的連線數打爆 ——
/// 與 Redis 的 IConnectionMultiplexer 是同一個道理。
///
/// Channel（IChannel）才是輕量的，但**不是執行緒安全**，
/// 所以發布端會為每次發布借用一個 Channel，不共用。
/// </summary>
public interface IRabbitMqConnectionProvider : IAsyncDisposable
{
    Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default);
}
