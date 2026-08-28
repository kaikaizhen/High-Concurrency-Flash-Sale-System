using FlashSale.Api.Infrastructure.Messaging;
using FlashSale.Api.Options;
using Microsoft.Extensions.Options;

namespace FlashSale.Api.Extensions;

public static class MessagingStartupExtensions
{
    /// <summary>
    /// 啟動時宣告 Exchange / Queue / Binding。
    ///
    /// API 與 Worker 共用這個方法，因為兩邊必須用完全一致的參數宣告 ——
    /// 參數不一致時 RabbitMQ 會回 PRECONDITION_FAILED。
    ///
    /// Broker 連不上時只記錄錯誤而不中斷啟動：同步搶購路徑
    /// （Atomic / Transaction / Optimistic）完全不需要 RabbitMQ，
    /// 讓整個 API 因為 Broker 沒起來就無法啟動並不合理。
    /// </summary>
    public static async Task DeclareMessagingTopologyAsync(
        this IServiceProvider services)
    {
        var options = services
            .GetRequiredService<IOptions<RabbitMqOptions>>()
            .Value;

        var logger = services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("MessagingStartup");

        if (!options.Enabled)
        {
            logger.LogInformation(
                "RabbitMQ disabled, skipping topology declaration.");

            return;
        }

        try
        {
            await services
                .GetRequiredService<RabbitMqTopology>()
                .DeclareAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to declare RabbitMQ topology. "
                + "Async purchase (AtomicQueued) will not work until the broker is reachable.");
        }
    }
}
