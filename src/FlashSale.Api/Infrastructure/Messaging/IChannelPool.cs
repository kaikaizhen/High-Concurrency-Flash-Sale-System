using RabbitMQ.Client;

namespace FlashSale.Api.Infrastructure.Messaging;

/// <summary>
/// 可重複使用的 Channel 池。
///
/// IChannel 不是執行緒安全的，所以不能全域共用一個；
/// 但每次發布都新建一個也不對 —— 開啟 Channel 需要一次 AMQP 往返，
/// 在高併發下這個成本會直接吃掉非同步化省下來的時間。
///
/// 折衷：租借 / 歸還。同一時間只有一個發布者持有某個 Channel，
/// 用完放回池子給下一個人用。
/// </summary>
public interface IChannelPool : IAsyncDisposable
{
    /// <summary>
    /// 借一個 Channel。使用 <c>await using</c> 確保歸還。
    /// </summary>
    Task<PooledChannel> RentAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 借出的 Channel。Dispose 時歸還而不是關閉。
/// </summary>
public sealed class PooledChannel : IAsyncDisposable
{
    private readonly ChannelPool _pool;
    private bool _returned;

    internal PooledChannel(ChannelPool pool, IChannel channel)
    {
        _pool = pool;
        Channel = channel;
    }

    public IChannel Channel { get; }

    public async ValueTask DisposeAsync()
    {
        if (_returned)
        {
            return;
        }

        _returned = true;

        await _pool.ReturnAsync(Channel);
    }
}
