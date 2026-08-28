using System.Collections.Concurrent;
using RabbitMQ.Client;

namespace FlashSale.Api.Infrastructure.Messaging;

public class ChannelPool : IChannelPool
{
    /// <summary>
    /// 池子的上限。超過時多出來的 Channel 用完就關閉而不放回，
    /// 避免尖峰過後長期佔著大量 Channel。
    /// </summary>
    private const int MaxPooledChannels = 64;

    private readonly IRabbitMqConnectionProvider _connectionProvider;
    private readonly ConcurrentBag<IChannel> _channels = new();
    private int _pooledCount;
    private bool _disposed;

    public ChannelPool(IRabbitMqConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public async Task<PooledChannel> RentAsync(
        CancellationToken cancellationToken = default)
    {
        while (_channels.TryTake(out var pooled))
        {
            Interlocked.Decrement(ref _pooledCount);

            // 池子裡的 Channel 可能在閒置期間被 Broker 或網路中斷關閉，
            // 拿出來時必須確認還活著，否則發布會直接失敗。
            if (pooled.IsOpen)
            {
                return new PooledChannel(this, pooled);
            }

            await pooled.DisposeAsync();
        }

        return new PooledChannel(this, await CreateChannelAsync(cancellationToken));
    }

    private async Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken)
    {
        var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);

        // Publisher Confirms 是 Channel 層級的設定，
        // 因此必須在建立時就開啟，不能事後才改。
        return await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);
    }

    internal async ValueTask ReturnAsync(IChannel channel)
    {
        if (_disposed || !channel.IsOpen ||
            Interlocked.Increment(ref _pooledCount) > MaxPooledChannels)
        {
            Interlocked.Decrement(ref _pooledCount);
            await channel.DisposeAsync();

            return;
        }

        _channels.Add(channel);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        while (_channels.TryTake(out var channel))
        {
            await channel.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
