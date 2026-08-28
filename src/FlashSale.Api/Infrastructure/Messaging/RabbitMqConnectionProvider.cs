using FlashSale.Api.Options;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FlashSale.Api.Infrastructure.Messaging;

public class RabbitMqConnectionProvider : IRabbitMqConnectionProvider
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnectionProvider> _logger;

    // 連線是延遲建立的：應用程式啟動時 Broker 不一定已經就緒，
    // 在建構子就連線會讓啟動失敗。
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public RabbitMqConnectionProvider(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqConnectionProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IConnection> GetConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            // double-check：等鎖期間可能已經有人建好了
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            // 設定沒讀到時 HostName 會是空字串，而 ConnectionFactory
            // 不會因此報錯 —— 它會嘗試連到一個解析出來的奇怪位址，
            // 錯誤訊息裡只看得到那個位址，完全看不出真正的原因是設定沒載入。
            if (string.IsNullOrWhiteSpace(_options.HostName))
            {
                throw new InvalidOperationException(
                    $"RabbitMq:HostName is not configured. "
                    + $"Check that appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json "
                    + "is present in the content root.");
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,

                // 連線中斷時自動重連，並重建 Channel 與 Consumer。
                // 沒有這個，Broker 重啟後 Worker 會靜默地停止消費。
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);

            _logger.LogInformation(
                "RabbitMQ connected. Host={Host}:{Port} VHost={VHost}",
                _options.HostName,
                _options.Port,
                _options.VirtualHost);

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _gate.Dispose();

        GC.SuppressFinalize(this);
    }
}
